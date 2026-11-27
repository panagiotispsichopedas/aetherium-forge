namespace AetheriumForge.Core.Rules;

/// <summary>
/// Artistic direction for one group (IMPLEMENTATION_GUIDE §8, "Look"). Every value defaults to neutral, which reproduces the plain
/// model output exactly.
/// </summary>
public sealed record LookSettings
{
    /// <summary>Gets the neutral look (all defaults).</summary>
    public static LookSettings Neutral { get; } = new();

    /// <summary>Gets the blend of the AI output with a plain Lanczos upscale: 1 = AI only, 0 = Lanczos only. Range 0–1.</summary>
    public double AiStrength { get; init; } = 1.0;

    /// <summary>Gets the unsharp mask applied after resizing (colour kinds only).</summary>
    public SharpenSettings Sharpen { get; init; } = SharpenSettings.None;

    /// <summary>Gets the denoise strength (0–1) for models that support it, or <see langword="null"/> for the model's default.</summary>
    public double? Denoise { get; init; }

    /// <summary>Gets the brightness offset in linear light, −1–1 (colour kinds only).</summary>
    public double Brightness { get; init; }

    /// <summary>Gets the contrast multiplier around mid-grey, 0.5–2 (colour kinds only).</summary>
    public double Contrast { get; init; } = 1.0;

    /// <summary>Gets the gamma, 0.5–2 (colour kinds only).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Gets the saturation multiplier, 0–2 (colour kinds only).</summary>
    public double Saturation { get; init; } = 1.0;

    /// <summary>Gets the colour temperature shift, −1 (cool) to 1 (warm) (colour kinds only).</summary>
    public double Temperature { get; init; }

    /// <summary>Gets the normal-map XY strength ("how 3D"); 1 keeps the relief unchanged.</summary>
    public double NormalStrength { get; init; } = 1.0;

    /// <summary>Gets the specular intensity multiplier ("how shiny"), 0–2, for specular maps and the specular alpha of normal maps.</summary>
    public double SpecularScale { get; init; } = 1.0;

    /// <summary>Gets a value indicating whether every control is at its neutral value.</summary>
    public bool IsNeutral => this == Neutral;
}

/// <summary>
/// Unsharp-mask settings.
/// </summary>
/// <param name="Amount">Strength; 0 disables sharpening.</param>
/// <param name="Radius">Gaussian radius in output pixels.</param>
public sealed record SharpenSettings(double Amount = 0.0, double Radius = 1.0)
{
    /// <summary>Gets the disabled setting.</summary>
    public static SharpenSettings None { get; } = new();
}
