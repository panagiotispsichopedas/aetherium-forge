using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// A named look preset of the Look panel.
/// </summary>
/// <param name="Name">The display name.</param>
/// <param name="Description">One line on what the preset does.</param>
/// <param name="Look">The look it applies.</param>
public sealed record LookPreset(string Name, string Description, LookSettings Look);

/// <summary>
/// The Look panel presets (IMPLEMENTATION_GUIDE §14, Compare). Each preset replaces the whole look; the user then fine-tunes the sliders.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Preset</term><description>Values (everything else neutral)</description></listheader>
/// <item><term>Neutral</term><description>All defaults: the plain model output.</description></item>
/// <item><term>Crisp</term><description>Sharpen 0.6 at radius 1.0, contrast 1.05. Adds edge definition to colour maps without halos.</description></item>
/// <item><term>Soft</term><description>AI strength 0.7, contrast 0.95. Tames the "painted", over-sharpened look of strong models.</description></item>
/// <item><term>Vivid</term><description>Saturation 1.25, contrast 1.1, temperature +0.05, sharpen 0.3 at radius 1.0. Richer, slightly warmer colour.</description></item>
/// <item><term>Deep relief</term><description>Normal strength 1.6, specular scale 1.15, sharpen 0.3 at radius 1.0. Deeper bumps and a little more shine.</description></item>
/// </list>
/// </remarks>
public static class LookPresets
{
    /// <summary>Gets the neutral preset.</summary>
    public static LookPreset Neutral { get; } = new("Neutral", "The plain model output", LookSettings.Neutral);

    /// <summary>Gets the crisp preset.</summary>
    public static LookPreset Crisp { get; } = new(
        "Crisp",
        "More edge definition",
        new LookSettings { Sharpen = new SharpenSettings(0.6, 1.0), Contrast = 1.05 });

    /// <summary>Gets the soft preset.</summary>
    public static LookPreset Soft { get; } = new(
        "Soft",
        "Tames over-sharpened AI detail",
        new LookSettings { AiStrength = 0.7, Contrast = 0.95 });

    /// <summary>Gets the vivid preset.</summary>
    public static LookPreset Vivid { get; } = new(
        "Vivid",
        "Richer, slightly warmer colour",
        new LookSettings { Saturation = 1.25, Contrast = 1.1, Temperature = 0.05, Sharpen = new SharpenSettings(0.3, 1.0) });

    /// <summary>Gets the deep-relief preset.</summary>
    public static LookPreset DeepRelief { get; } = new(
        "Deep relief",
        "Deeper bumps and a little more shine",
        new LookSettings { NormalStrength = 1.6, SpecularScale = 1.15, Sharpen = new SharpenSettings(0.3, 1.0) });

    /// <summary>Gets every preset, in display order.</summary>
    public static IReadOnlyList<LookPreset> All { get; } = [Neutral, Crisp, Soft, Vivid, DeepRelief];
}
