using AetheriumForge.Core.Textures;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The look controls of the Compare page's Look panel (IMPLEMENTATION_GUIDE §8, "Look").
/// </summary>
public enum LookControl
{
    /// <summary>Blend of the AI output with a plain Lanczos upscale.</summary>
    AiStrength,

    /// <summary>Unsharp-mask amount.</summary>
    SharpenAmount,

    /// <summary>Unsharp-mask radius.</summary>
    SharpenRadius,

    /// <summary>Denoise strength (needs a denoise model; always disabled for now).</summary>
    Denoise,

    /// <summary>Brightness offset.</summary>
    Brightness,

    /// <summary>Contrast multiplier.</summary>
    Contrast,

    /// <summary>Gamma.</summary>
    Gamma,

    /// <summary>Saturation multiplier.</summary>
    Saturation,

    /// <summary>Colour temperature, cool to warm.</summary>
    Temperature,

    /// <summary>Normal-map XY strength ("how 3D").</summary>
    NormalStrength,

    /// <summary>Specular intensity ("how shiny").</summary>
    SpecularScale,
}

/// <summary>
/// The slider range of one look control.
/// </summary>
/// <param name="Control">The control.</param>
/// <param name="Label">The slider label.</param>
/// <param name="Minimum">The smallest value.</param>
/// <param name="Maximum">The largest value.</param>
/// <param name="Neutral">The neutral (default) value.</param>
/// <param name="Step">The slider step.</param>
public sealed record LookControlRange(LookControl Control, string Label, double Minimum, double Maximum, double Neutral, double Step)
{
    /// <summary>Gets whether <paramref name="value"/> is inside [<see cref="Minimum"/>, <see cref="Maximum"/>].</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value is in range.</returns>
    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    /// <summary>Clamps <paramref name="value"/> to the range.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The clamped value.</returns>
    public double Clamp(double value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// Slider ranges and per-kind applicability of the look controls. Ranges follow the documented <see cref="Core.Rules.LookSettings"/>
/// ranges; the normal-strength and sharpen ranges, which the guide leaves open, are UI limits.
/// </summary>
public static class LookControls
{
    /// <summary>The tooltip on the disabled denoise slider.</summary>
    public const string DenoiseTooltip = "needs a denoise model";

    private static readonly Dictionary<LookControl, LookControlRange> RangeMap = new()
    {
        [LookControl.AiStrength] = new(LookControl.AiStrength, "AI strength", 0.0, 1.0, 1.0, 0.05),
        [LookControl.SharpenAmount] = new(LookControl.SharpenAmount, "Sharpen", 0.0, 2.0, 0.0, 0.05),
        [LookControl.SharpenRadius] = new(LookControl.SharpenRadius, "Sharpen radius", 0.3, 3.0, 1.0, 0.1),
        [LookControl.Denoise] = new(LookControl.Denoise, "Denoise", 0.0, 1.0, 0.0, 0.05),
        [LookControl.Brightness] = new(LookControl.Brightness, "Brightness", -1.0, 1.0, 0.0, 0.01),
        [LookControl.Contrast] = new(LookControl.Contrast, "Contrast", 0.5, 2.0, 1.0, 0.01),
        [LookControl.Gamma] = new(LookControl.Gamma, "Gamma", 0.5, 2.0, 1.0, 0.01),
        [LookControl.Saturation] = new(LookControl.Saturation, "Saturation", 0.0, 2.0, 1.0, 0.01),
        [LookControl.Temperature] = new(LookControl.Temperature, "Temperature", -1.0, 1.0, 0.0, 0.01),
        [LookControl.NormalStrength] = new(LookControl.NormalStrength, "Relief", 0.0, 4.0, 1.0, 0.05),
        [LookControl.SpecularScale] = new(LookControl.SpecularScale, "Shine", 0.0, 2.0, 1.0, 0.05),
    };

    /// <summary>Gets the controls that have a slider value, in display order (every control but Denoise).</summary>
    public static IReadOnlyList<LookControl> Editable { get; } =
    [
        LookControl.AiStrength, LookControl.SharpenAmount, LookControl.SharpenRadius,
        LookControl.Brightness, LookControl.Contrast, LookControl.Gamma, LookControl.Saturation, LookControl.Temperature,
        LookControl.NormalStrength, LookControl.SpecularScale,
    ];

    /// <summary>Gets the Look panel section of a control.</summary>
    /// <param name="control">The control.</param>
    /// <returns>"Detail", "Colour" or "Surface".</returns>
    public static string SectionOf(LookControl control) => control switch
    {
        LookControl.AiStrength or LookControl.SharpenAmount or LookControl.SharpenRadius or LookControl.Denoise => "Detail",
        LookControl.NormalStrength or LookControl.SpecularScale => "Surface",
        _ => "Colour",
    };

    /// <summary>Gets every control's range.</summary>
    public static IReadOnlyDictionary<LookControl, LookControlRange> Ranges => RangeMap;

    /// <summary>Gets the range of <paramref name="control"/>.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The range.</returns>
    public static LookControlRange RangeOf(LookControl control) => RangeMap[control];

    /// <summary>Gets whether <paramref name="kind"/> is a colour kind (colour grading and sharpening apply).</summary>
    /// <param name="kind">The texture kind.</param>
    /// <returns><see langword="true"/> for diffuse, glow and backlight maps.</returns>
    public static bool IsColourKind(TextureKind kind) => kind is TextureKind.Diffuse or TextureKind.Glow or TextureKind.Backlight;

    /// <summary>
    /// Gets whether <paramref name="control"/> changes a texture of <paramref name="kind"/> (§8 table).
    /// </summary>
    /// <param name="control">The control.</param>
    /// <param name="kind">The sample's texture kind.</param>
    /// <param name="usesModel">Whether the sample runs through an AI model (false for parallax maps and jobs without a model).</param>
    /// <returns><see langword="true"/> when the control applies.</returns>
    public static bool AppliesTo(LookControl control, TextureKind kind, bool usesModel) => control switch
    {
        LookControl.AiStrength => usesModel && kind != TextureKind.Parallax,
        LookControl.SharpenAmount or LookControl.SharpenRadius => IsColourKind(kind),
        LookControl.Denoise => false,
        LookControl.Brightness or LookControl.Contrast or LookControl.Gamma or LookControl.Saturation or LookControl.Temperature => IsColourKind(kind),
        LookControl.NormalStrength => kind == TextureKind.Normal,
        LookControl.SpecularScale => kind is TextureKind.Specular or TextureKind.Normal,
        _ => false,
    };

    /// <summary>Gets the tooltip shown on a control that does not apply.</summary>
    /// <param name="control">The control.</param>
    /// <param name="kind">The sample's texture kind.</param>
    /// <param name="usesModel">Whether the sample runs through an AI model.</param>
    /// <returns>The reason, or <see langword="null"/> when the control applies.</returns>
    public static string? DisabledReason(LookControl control, TextureKind kind, bool usesModel)
    {
        if (control == LookControl.Denoise)
        {
            return DenoiseTooltip;
        }

        if (AppliesTo(control, kind, usesModel))
        {
            return null;
        }

        return control == LookControl.AiStrength
            ? "No AI model runs for this texture (Lanczos only)"
            : $"Does not apply to {kind} maps";
    }
}
