using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Game;

/// <summary>
/// The resolved view of which source wins for each resource path, and the gap set derived from it (IMPLEMENTATION_GUIDE §5).
/// </summary>
/// <remarks>All paths are canonicalised with <see cref="GamePath.Normalize"/>, so comparison is case-insensitive with backslashes.</remarks>
public sealed class ResourceGraph
{
    private const string TexturesPrefix = @"textures\";
    private const string DdsExtension = ".dds";

    private readonly HashSet<string> _looseFiles;
    private readonly HashSet<string> _pluginArchiveFiles;
    private readonly Dictionary<string, string> _iniArchiveFiles;

    /// <summary>
    /// Initialises a new graph.
    /// </summary>
    /// <param name="looseFiles">Resource paths present as loose files in Data.</param>
    /// <param name="pluginArchiveFiles">Resource paths contained in any active plugin archive.</param>
    /// <param name="iniArchiveFiles">
    /// Resource path to the INI archive that provides it (the caller resolves duplicates so the last INI archive wins).
    /// Keys that differ only by case or separator are merged; the last one enumerated wins.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ResourceGraph(IEnumerable<string> looseFiles, IEnumerable<string> pluginArchiveFiles, IReadOnlyDictionary<string, string> iniArchiveFiles)
    {
        ArgumentNullException.ThrowIfNull(looseFiles);
        ArgumentNullException.ThrowIfNull(pluginArchiveFiles);
        ArgumentNullException.ThrowIfNull(iniArchiveFiles);

        _looseFiles = new HashSet<string>(looseFiles.Select(GamePath.Normalize), GamePath.Comparer);
        _pluginArchiveFiles = new HashSet<string>(pluginArchiveFiles.Select(GamePath.Normalize), GamePath.Comparer);
        _iniArchiveFiles = new Dictionary<string, string>(GamePath.Comparer);
        foreach (KeyValuePair<string, string> pair in iniArchiveFiles)
        {
            _iniArchiveFiles[GamePath.Normalize(pair.Key)] = pair.Value;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> exists as a loose file in Data (loose files override every archive).
    /// </summary>
    /// <param name="path">A resource path in any case or separator style.</param>
    /// <returns><see langword="true"/> when a loose file exists for this path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public bool IsLoose(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return _looseFiles.Contains(GamePath.Normalize(path));
    }

    /// <summary>
    /// Returns the INI archive that the game actually serves <paramref name="path"/> from, when no loose file or plugin archive overrides it.
    /// </summary>
    /// <param name="path">A resource path in any case or separator style.</param>
    /// <returns>The INI archive source, or <see langword="null"/> when the path is overridden or not provided by an INI archive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public ResourceSource? GetWinningIniSource(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string canonical = GamePath.Normalize(path);
        if (_looseFiles.Contains(canonical) || _pluginArchiveFiles.Contains(canonical))
        {
            return null;
        }

        return _iniArchiveFiles.TryGetValue(canonical, out string? archive) ? new ResourceSource(archive) : null;
    }

    /// <summary>
    /// Computes the gap set: INI-archive <c>*.dds</c> files under <c>textures\</c> with no loose override, no plugin archive override,
    /// and not excluded.
    /// </summary>
    /// <param name="isExcluded">Optional veto on canonical paths (rules module); <see langword="true"/> removes the path.</param>
    /// <returns>Gap entries sorted by canonical path (ordinal).</returns>
    public IReadOnlyList<GapEntry> ComputeGapSet(Func<string, bool>? isExcluded = null)
    {
        var result = new List<GapEntry>();
        foreach (KeyValuePair<string, string> pair in _iniArchiveFiles)
        {
            string path = pair.Key;
            if (!path.StartsWith(TexturesPrefix, StringComparison.Ordinal) || !path.EndsWith(DdsExtension, StringComparison.Ordinal))
            {
                continue;
            }

            if (_looseFiles.Contains(path) || _pluginArchiveFiles.Contains(path))
            {
                continue;
            }

            if (isExcluded is not null && isExcluded(path))
            {
                continue;
            }

            result.Add(new GapEntry(path, pair.Value));
        }

        result.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return result;
    }
}
