namespace AetheriumForge.Core.Textures;

/// <summary>
/// The role of a texture, derived from its file-name suffix (IMPLEMENTATION_GUIDE §8).
/// </summary>
public enum TextureKind
{
    /// <summary>Diffuse / albedo map: no suffix or <c>_d</c>. Upscaled with alpha kept.</summary>
    Diffuse,

    /// <summary>Tangent-space normal map (<c>_n</c>; alpha holds specular). Upscaled and renormalised.</summary>
    Normal,

    /// <summary>Model-space normal map (<c>_msn</c>). Skipped by default.</summary>
    ModelSpaceNormal,

    /// <summary>Specular map (<c>_s</c>). Upscaled.</summary>
    Specular,

    /// <summary>Glow / emissive map (<c>_g</c>). Upscaled.</summary>
    Glow,

    /// <summary>Height / parallax map (<c>_p</c>). Upscaled without AI (Lanczos).</summary>
    Parallax,

    /// <summary>Environment map or environment mask (<c>_e</c>, <c>_em</c>, <c>_m</c>). Skipped by default.</summary>
    Environment,

    /// <summary>Environment mask with the explicit <c>_envmask</c> suffix. Upscaling is fine.</summary>
    EnvironmentMask,

    /// <summary>Skin tint map (<c>_sk</c>). Skipped by default.</summary>
    SkinTint,

    /// <summary>Backlight map (<c>_b</c>, <c>_bl</c>). Upscaled.</summary>
    Backlight,
}
