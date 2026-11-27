using System.Text;
using System.Text.RegularExpressions;
using AetheriumForge.Core.Common;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Applies a <see cref="ForgeRules"/> rule set to textures. Glob patterns are compiled once per group at construction.
/// The evaluator is immutable and thread-safe.
/// </summary>
public sealed class RuleEvaluator
{
    private readonly ForgeRules _rules;
    private readonly CompiledGroup[] _groups;
    private readonly string[] _excludedFolders;

    /// <summary>
    /// Initialises a new evaluator for <paramref name="rules"/>.
    /// </summary>
    /// <param name="rules">The rule set. Later changes to it are not observed (records are immutable).</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    public RuleEvaluator(ForgeRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules;
        _groups = [.. rules.Groups.Select(g => new CompiledGroup(g, CompileGlobs(g.Match)))];
        _excludedFolders = ForgeRules.NormalizeFolders(rules.Exclude);
    }

    /// <summary>
    /// Decides whether and how to upscale one texture. Checks run in this order: excluded folder, skipped kind, invalid header,
    /// cube map, first matching group (top-down), minimum source, cap. The target is <c>min(cap, sourceMax × maxFactor)</c> for the
    /// larger dimension, with the aspect ratio kept (each dimension rounded, at least 1).
    /// </summary>
    /// <param name="canonicalPath">The canonical game path of the texture, for example <c>textures\clutter\bucket01.dds</c>.</param>
    /// <param name="header">The parsed DDS header.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="canonicalPath"/> is <see langword="null"/>.</exception>
    public TextureDecision Evaluate(string canonicalPath, DdsHeader header)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        string path = GamePath.Normalize(canonicalPath);
        TextureKind kind = TextureClassifier.GetKind(path);

        if (ForgeRules.IsUnderAny(path, _excludedFolders))
        {
            return Skipped(SkipReason.ExcludedFolder, kind);
        }

        if (TextureClassifier.IsSkippedByDefault(kind))
        {
            return Skipped(SkipReason.SkippedKind, kind);
        }

        if (header.Width <= 0 || header.Height <= 0)
        {
            return Skipped(SkipReason.InvalidHeader, kind);
        }

        if (header.IsCubemap)
        {
            return Skipped(SkipReason.Cubemap, kind);
        }

        int sourceMax = header.MaxDimension;
        GroupRule? group = FindGroup(path, kind, sourceMax);
        if (group is null)
        {
            return Skipped(SkipReason.NoMatchingGroup, kind);
        }

        if (sourceMax < _rules.MinSource)
        {
            return Skipped(SkipReason.TooSmall, kind, group.Name);
        }

        if (sourceMax >= group.Cap)
        {
            return Skipped(SkipReason.AlreadyAtCap, kind, group.Name);
        }

        long targetMax = Math.Min(group.Cap, (long)sourceMax * group.MaxFactor);
        int width = Scale(header.Width, targetMax, sourceMax);
        int height = Scale(header.Height, targetMax, sourceMax);
        LookSettings look = LookResolver.Resolve(_rules, group.Name, null);
        return new TextureDecision(SkipReason.None, kind, group.Name, width, height, group.Model, look.NormalStrength, look);
    }

    internal static Regex CompileGlobs(IEnumerable<string> patterns)
    {
        var builder = new StringBuilder("^(?:");
        bool first = true;
        foreach (string pattern in patterns)
        {
            if (!first)
            {
                builder.Append('|');
            }

            first = false;
            foreach (char c in pattern.Replace('/', '\\'))
            {
                switch (c)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        builder.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }
        }

        builder.Append(")$");
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);
    }

    private static int Scale(int dimension, long targetMax, int sourceMax) =>
        (int)Math.Max(1, Math.Round((double)dimension * targetMax / sourceMax, MidpointRounding.AwayFromZero));

    private static TextureDecision Skipped(SkipReason reason, TextureKind kind, string? group = null) =>
        new(reason, kind, group, 0, 0, null, 0);

    private GroupRule? FindGroup(string canonicalPath, TextureKind kind, int sourceMax)
    {
        foreach (CompiledGroup compiled in _groups)
        {
            GroupRule rule = compiled.Rule;
            if (rule.MinSourceMax is int min && sourceMax < min)
            {
                continue;
            }

            if (rule.MaxSourceMax is int max && sourceMax > max)
            {
                continue;
            }

            if (rule.Kinds is { } kinds && !kinds.Contains(kind))
            {
                continue;
            }

            if (compiled.Pattern.IsMatch(canonicalPath))
            {
                return rule;
            }
        }

        return null;
    }

    private sealed record CompiledGroup(GroupRule Rule, Regex Pattern);
}
