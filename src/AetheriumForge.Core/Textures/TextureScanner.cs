using System.Collections.Concurrent;
using AetheriumForge.Core.Archives;
using AetheriumForge.Core.Common;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Textures;

/// <summary>
/// Computes the gap set for a game profile (IMPLEMENTATION_GUIDE §5) and classifies every texture in it (§8).
/// </summary>
/// <remarks>
/// <para>Tiers, lowest to highest priority:</para>
/// <list type="number">
/// <item>Base archives: the INI archive list, then the archives of the implicit masters and of the Creation Club plugins
/// listed in <c>Skyrim.ccc</c>, in load order. These are the gap sources; a later base archive wins over an earlier one.</item>
/// <item>Mod archives: the archives of every other active plugin. Any texture they contain is provided by a mod.</item>
/// <item>Loose files under <c>Data\textures</c>. They always win.</item>
/// </list>
/// </remarks>
public sealed partial class TextureScanner
{
    private const string TexturesPrefix = @"textures\";
    private const string DdsExtension = ".dds";
    private const string CccFileName = "Skyrim.ccc";

    private readonly ILogger<TextureScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextureScanner"/> class.
    /// </summary>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    public TextureScanner(ILogger<TextureScanner>? logger = null) => _logger = logger ?? NullLogger<TextureScanner>.Instance;

    /// <summary>
    /// Scans the game described by <paramref name="profile"/> and applies <paramref name="rules"/> to the gap set.
    /// </summary>
    /// <param name="profile">The loaded game profile.</param>
    /// <param name="rules">The rules that decide groups, targets and skips.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The gap set with one decision per texture.</returns>
    public async Task<ScanResult> ScanAsync(GameProfile profile, ForgeRules rules, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rules);

        var dataFolder = profile.DataFolder;
        var missing = new List<string>();

        var loose = await Task.Run(() => EnumerateLooseTextures(dataFolder, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

        var (baseArchiveNames, modArchiveNames) = SplitArchives(profile);

        var modFiles = new HashSet<string>(StringComparer.Ordinal);
        var modDone = 0;
        foreach (var archiveName in modArchiveNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var reader = TryOpen(dataFolder, archiveName, missing))
            {
                if (reader is not null)
                {
                    foreach (var entry in reader.Entries)
                    {
                        if (IsTexture(entry.Path))
                        {
                            modFiles.Add(entry.Path);
                        }
                    }
                }
            }

            progress?.Report(new ScanProgress(ScanPhase.ModArchives, ++modDone, modArchiveNames.Count));
        }

        var readers = new Dictionary<string, BsaReader>(GamePath.Comparer);
        try
        {
            var baseFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            var baseDone = 0;
            foreach (var archiveName in baseArchiveNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reader = TryOpen(dataFolder, archiveName, missing);
                if (reader is not null)
                {
                    readers[archiveName] = reader;
                    foreach (var entry in reader.Entries)
                    {
                        if (IsTexture(entry.Path))
                        {
                            baseFiles[entry.Path] = archiveName;
                        }
                    }
                }

                progress?.Report(new ScanProgress(ScanPhase.BaseArchives, ++baseDone, baseArchiveNames.Count));
            }

            var graph = new ResourceGraph(loose, modFiles, baseFiles);
            var gaps = graph.ComputeGapSet(rules.IsExcluded);
            LogGraph(loose.Count, modFiles.Count, baseFiles.Count, gaps.Count);

            var textures = ReadHeadersAndDecide(gaps, readers, new RuleEvaluator(rules), progress, cancellationToken);

            return new ScanResult(textures, loose.Count, modArchiveNames.Count - CountMissing(modArchiveNames, missing), readers.Count, missing);
        }
        finally
        {
            foreach (var reader in readers.Values)
            {
                reader.Dispose();
            }
        }
    }

    private static bool IsTexture(string canonicalPath) =>
        canonicalPath.StartsWith(TexturesPrefix, StringComparison.Ordinal) && canonicalPath.EndsWith(DdsExtension, StringComparison.Ordinal);

    private static int CountMissing(IReadOnlyList<string> names, List<string> missing) => names.Count(n => missing.Contains(n, GamePath.Comparer));

    private static HashSet<string> EnumerateLooseTextures(string dataFolder, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var texturesFolder = Path.Combine(dataFolder, "textures");
        if (!Directory.Exists(texturesFolder))
        {
            return result;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var file in Directory.EnumerateFiles(texturesFolder, "*.dds", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(GamePath.Normalize(Path.GetRelativePath(dataFolder, file)));
            if (result.Count % 1000 == 0)
            {
                progress?.Report(new ScanProgress(ScanPhase.LooseFiles, result.Count, 0));
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.LooseFiles, result.Count, result.Count));
        return result;
    }

    private static (List<string> BaseArchives, List<string> ModArchives) SplitArchives(GameProfile profile)
    {
        var gameFolder = Path.GetDirectoryName(Path.GetFullPath(profile.DataFolder)) ?? profile.DataFolder;
        var cccPath = Path.Combine(gameFolder, CccFileName);
        var basePlugins = new HashSet<string>(LoadOrderBuilder.ImplicitMasters, GamePath.Comparer);
        if (File.Exists(cccPath))
        {
            basePlugins.UnionWith(PluginListParser.ParseCcc(File.ReadAllText(cccPath)));
        }

        bool Exists(string name) => File.Exists(Path.Combine(profile.DataFolder, name));

        var baseLoadOrder = profile.LoadOrder.Where(basePlugins.Contains).ToList();
        var baseArchives = new List<string>(profile.IniArchives);
        foreach (var archive in PluginArchives.For(baseLoadOrder, Exists))
        {
            if (!baseArchives.Contains(archive, GamePath.Comparer))
            {
                baseArchives.Add(archive);
            }
        }

        var modArchives = profile.PluginArchives
            .Where(a => !baseArchives.Contains(a, GamePath.Comparer))
            .ToList();

        return (baseArchives, modArchives);
    }

    private BsaReader? TryOpen(string dataFolder, string archiveName, List<string> missing)
    {
        var path = Path.Combine(dataFolder, archiveName);
        if (!File.Exists(path))
        {
            missing.Add(archiveName);
            LogMissingArchive(archiveName);
            return null;
        }

        try
        {
            return BsaReader.Open(path);
        }
        catch (InvalidDataException ex)
        {
            LogUnreadableArchive(ex, archiveName);
            missing.Add(archiveName);
            return null;
        }
    }

    private List<ScannedTexture> ReadHeadersAndDecide(
        IReadOnlyList<GapEntry> gaps,
        Dictionary<string, BsaReader> readers,
        RuleEvaluator evaluator,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entryLookup = new Dictionary<(string Archive, string Path), BsaEntry>();
        foreach (var (name, reader) in readers)
        {
            foreach (var entry in reader.Entries)
            {
                entryLookup[(name.ToLowerInvariant(), entry.Path)] = entry;
            }
        }

        var results = new ScannedTexture[gaps.Count];
        var done = 0;
        var failures = new ConcurrentBag<string>();
        var options = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, gaps.Count, options, () => new byte[DdsHeader.MinimumLength], (i, _, buffer) =>
        {
            var gap = gaps[i];
            DdsHeader? header = null;
            if (readers.TryGetValue(gap.SourceArchive, out var reader)
                && entryLookup.TryGetValue((gap.SourceArchive.ToLowerInvariant(), gap.Path), out var entry))
            {
                var length = reader.ReadPrefix(entry, buffer);
                if (DdsHeader.TryParse(buffer.AsSpan(0, length), out var parsed))
                {
                    header = parsed;
                }
            }

            if (header is null)
            {
                failures.Add(gap.Path);
            }

            var decision = evaluator.Evaluate(gap.Path, header ?? default);
            results[i] = new ScannedTexture(gap.Path, gap.SourceArchive, header, decision);

            var current = Interlocked.Increment(ref done);
            if (current % 250 == 0 || current == gaps.Count)
            {
                progress?.Report(new ScanProgress(ScanPhase.Headers, current, gaps.Count));
            }

            return buffer;
        }, _ => { });

        if (!failures.IsEmpty)
        {
            LogHeaderFailures(failures.Count);
        }

        return [.. results];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Archive {Archive} is referenced but not present in Data; skipping it")]
    private partial void LogMissingArchive(string archive);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Archive {Archive} could not be read; skipping it")]
    private partial void LogUnreadableArchive(Exception exception, string archive);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resource graph: {Loose} loose textures, {Mod} mod-archive textures, {Base} base textures, {Gaps} gaps")]
    private partial void LogGraph(int loose, int mod, int @base, int gaps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} gap textures have an unreadable DDS header and are marked invalid")]
    private partial void LogHeaderFailures(int count);
}
