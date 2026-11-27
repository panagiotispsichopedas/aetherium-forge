namespace AetheriumForge.Core.Rules;

/// <summary>
/// Why a texture is not upscaled.
/// </summary>
public enum SkipReason
{
    /// <summary>The texture is upscaled.</summary>
    None,

    /// <summary>The texture is inside an excluded folder.</summary>
    ExcludedFolder,

    /// <summary>The texture kind is skipped by default (model-space normal, environment, skin tint).</summary>
    SkippedKind,

    /// <summary>The texture is a cube map.</summary>
    Cubemap,

    /// <summary>The source max dimension is already at or above the group's cap.</summary>
    AlreadyAtCap,

    /// <summary>The source max dimension is below the minimum source size.</summary>
    TooSmall,

    /// <summary>No group matches the texture.</summary>
    NoMatchingGroup,

    /// <summary>The DDS header is missing or invalid (non-positive dimensions).</summary>
    InvalidHeader,
}
