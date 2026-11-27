using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// The outcome of evaluating the rules for one texture.
/// </summary>
/// <param name="Skip">Why the texture is skipped, or <see cref="SkipReason.None"/> when it is upscaled.</param>
/// <param name="Kind">The texture kind derived from the file name.</param>
/// <param name="Group">The name of the matching group, or <see langword="null"/> when the decision was made before a group matched.</param>
/// <param name="TargetWidth">The output width in pixels; 0 when skipped.</param>
/// <param name="TargetHeight">The output height in pixels; 0 when skipped.</param>
/// <param name="Model">The upscaling model; <see langword="null"/> when skipped.</param>
/// <param name="NormalStrength">The group's normal-map strength (used for normal maps); 0 when skipped.</param>
public sealed record TextureDecision(SkipReason Skip, TextureKind Kind, string? Group, int TargetWidth, int TargetHeight, string? Model, double NormalStrength)
{
    /// <summary>Gets a value indicating whether the texture is skipped.</summary>
    public bool IsSkipped => Skip != SkipReason.None;
}
