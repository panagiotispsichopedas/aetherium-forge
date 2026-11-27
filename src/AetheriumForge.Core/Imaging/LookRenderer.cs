using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// The images a look is applied to, all already resized to the target size.
/// </summary>
/// <param name="AiRgb">The model's RGB output, resized to the target.</param>
/// <param name="LanczosRgb">A plain Lanczos upscale of the source RGB to the target (for <see cref="LookSettings.AiStrength"/>); may be the same instance as <paramref name="AiRgb"/> when no model ran.</param>
/// <param name="Alpha">The alpha channel at the target size, or <see langword="null"/> when the source has none.</param>
/// <param name="Kind">The texture kind; decides which controls apply.</param>
public sealed record LookInput(PlanarImage AiRgb, PlanarImage LanczosRgb, PlanarImage? Alpha, TextureKind Kind);

/// <summary>
/// Applies a group's <see cref="LookSettings"/> after resizing (IMPLEMENTATION_GUIDE §8 order: AI-strength blend, sharpen, colour
/// grading for colour kinds, normal strength for normal maps, specular scale, merge alpha). Used by the texture processor and by the
/// Compare screen's live preview, which renders from a cached upscale.
/// </summary>
/// <remarks>
/// M5 scaffold: only the neutral path and normal strength are implemented here; the M5 look work implements the other controls behind
/// this same signature. A neutral look must reproduce the M2 output exactly.
/// </remarks>
public static class LookRenderer
{
    /// <summary>
    /// Renders the final RGBA image for <paramref name="input"/> with <paramref name="look"/>. The inputs are not modified.
    /// </summary>
    /// <param name="input">The resized images and kind.</param>
    /// <param name="look">The look; <see langword="null"/> means neutral.</param>
    /// <returns>The RGBA image to encode.</returns>
    public static RgbaImage Render(LookInput input, LookSettings? look)
    {
        ArgumentNullException.ThrowIfNull(input);

        look ??= LookSettings.Neutral;
        var rgb = new PlanarImage(input.AiRgb.Channels, input.AiRgb.Width, input.AiRgb.Height, (float[])input.AiRgb.Data.Clone());
        if (input.Kind == TextureKind.Normal)
        {
            ImageOps.RenormalizeNormals(rgb, look.NormalStrength);
        }

        return ImageOps.ToRgba(rgb, input.Alpha);
    }
}
