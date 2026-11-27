using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// One editable upscaling group (IMPLEMENTATION_GUIDE §8). Groups are evaluated top-down and the first matching group wins.
/// </summary>
/// <remarks>
/// A texture matches when its canonical path matches any <see cref="Match"/> pattern, its source max is inside
/// [<see cref="MinSourceMax"/>, <see cref="MaxSourceMax"/>] (inclusive, each bound optional) and its kind is in <see cref="Kinds"/> (when set).
/// </remarks>
public sealed record GroupRule
{
    /// <summary>Gets the display name of the group, for example <c>Clutter</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the path patterns. <c>*</c> matches any characters including <c>\</c>, <c>?</c> matches one character,
    /// matching is case-insensitive against the canonical path, and <c>/</c> is treated as <c>\</c>. Defaults to <c>["*"]</c> (every path).
    /// </summary>
    public IReadOnlyList<string> Match { get; init; } = ["*"];

    /// <summary>Gets the inclusive lower bound on the source max dimension, or <see langword="null"/> for no bound.</summary>
    public int? MinSourceMax { get; init; }

    /// <summary>Gets the inclusive upper bound on the source max dimension, or <see langword="null"/> for no bound.</summary>
    public int? MaxSourceMax { get; init; }

    /// <summary>Gets the texture kinds this group applies to, or <see langword="null"/> for every kind.</summary>
    public IReadOnlyList<TextureKind>? Kinds { get; init; }

    /// <summary>Gets the maximum output dimension in pixels. Defaults to 4096.</summary>
    public int Cap { get; init; } = 4096;

    /// <summary>Gets the maximum scale factor applied to the source. Defaults to 4.</summary>
    public int MaxFactor { get; init; } = 4;

    /// <summary>Gets the upscaling model identifier, for example <c>realesrgan-x4plus</c>.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the normal-map strength multiplier applied when renormalising normal maps. Defaults to 1.0.</summary>
    public double NormalStrength { get; init; } = 1.0;

    /// <summary>
    /// Gets the group's artistic look (§8), or <see langword="null"/> for neutral. JSON persistence and the <see cref="NormalStrength"/>
    /// alias are wired by the M5 look work.
    /// </summary>
    public LookSettings? Look { get; init; }

    /// <summary>Determines whether two rules have equal values, comparing <see cref="Match"/> and <see cref="Kinds"/> element by element.</summary>
    /// <param name="other">The rule to compare with.</param>
    /// <returns><see langword="true"/> when all values are equal.</returns>
    public bool Equals(GroupRule? other) =>
        other is not null
        && Name == other.Name
        && Match.SequenceEqual(other.Match)
        && MinSourceMax == other.MinSourceMax
        && MaxSourceMax == other.MaxSourceMax
        && (Kinds is null ? other.Kinds is null : other.Kinds is not null && Kinds.SequenceEqual(other.Kinds))
        && Cap == other.Cap
        && MaxFactor == other.MaxFactor
        && Model == other.Model
        && NormalStrength.Equals(other.NormalStrength)
        && Equals(Look, other.Look);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        foreach (string pattern in Match)
        {
            hash.Add(pattern);
        }

        hash.Add(MinSourceMax);
        hash.Add(MaxSourceMax);
        hash.Add(Kinds?.Count ?? -1);
        hash.Add(Cap);
        hash.Add(MaxFactor);
        hash.Add(Model);
        hash.Add(NormalStrength);
        hash.Add(Look);
        return hash.ToHashCode();
    }
}
