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
/// <list type="bullet">
/// <item><b>AI strength</b> (every kind): <c>lanczos + s × (ai − lanczos)</c>. The blend runs at the target size; resizing is linear,
/// so this matches blending before the resize up to the per-pass clamp.</item>
/// <item><b>Sharpen</b> (colour kinds only): unsharp mask with a separable Gaussian (<see cref="ImageOps.UnsharpMask"/>).</item>
/// <item><b>Colour grading</b> (colour kinds only): brightness, contrast, saturation and temperature in linear light, then gamma on the
/// perceptual value (<see cref="ImageOps.ColorGrade"/>).</item>
/// <item><b>Normal strength</b> (<see cref="TextureKind.Normal"/> only): the existing renormalisation.</item>
/// <item><b>Specular scale</b>: the RGB of <see cref="TextureKind.Specular"/> maps and the alpha (specular) of normal maps, clamped to 0–1.</item>
/// </list>
/// <para>Colour kinds are <see cref="TextureKind.Diffuse"/>, <see cref="TextureKind.Glow"/> and <see cref="TextureKind.Backlight"/>. Every
/// other kind (normal, model-space normal, specular, environment masks, parallax, ...) encodes data, so it is never sharpened or graded.</para>
/// <para>A control at its neutral value is skipped entirely, so a neutral look reproduces the M2 output byte for byte.</para>
/// <para><see cref="LookSettings.Denoise"/> is not applied here: it selects a model variant at load time.</para>
/// </remarks>
public static class LookRenderer
{
    /// <summary>
    /// Gets whether <paramref name="kind"/> holds colour (diffuse, glow, backlight), so sharpening and colour grading apply.
    /// </summary>
    /// <param name="kind">The texture kind.</param>
    /// <returns><see langword="true"/> for colour kinds.</returns>
    public static bool IsColorKind(TextureKind kind) => kind is TextureKind.Diffuse or TextureKind.Glow or TextureKind.Backlight;

    /// <summary>
    /// Gets whether a look blends the AI output with a plain Lanczos upscale, so the texture processor must keep the source RGB and
    /// resize it too (only when <see cref="LookSettings.AiStrength"/> is below 1).
    /// </summary>
    /// <param name="look">The look; <see langword="null"/> means neutral.</param>
    /// <returns><see langword="true"/> when the AI output is blended with a Lanczos upscale.</returns>
    public static bool NeedsLanczos(LookSettings? look) => look is not null && look.AiStrength < 1.0;

    /// <summary>
    /// Renders the final RGBA image for <paramref name="input"/> with <paramref name="look"/>. The inputs are not modified.
    /// </summary>
    /// <param name="input">The resized images and kind.</param>
    /// <param name="look">The look; <see langword="null"/> means neutral.</param>
    /// <returns>The RGBA image to encode.</returns>
    /// <exception cref="ArgumentException">The images in <paramref name="input"/> differ in size.</exception>
    public static RgbaImage Render(LookInput input, LookSettings? look)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.AiRgb);
        ArgumentNullException.ThrowIfNull(input.LanczosRgb);

        look ??= LookSettings.Neutral;
        PlanarImage rgb = input.AiRgb;
        bool owned = false;

        // 1. AI-strength blend (the blend allocates a new image, which later steps may modify in place).
        if (look.AiStrength < 1.0 && !ReferenceEquals(input.AiRgb, input.LanczosRgb))
        {
            rgb = ImageOps.Blend(input.AiRgb, input.LanczosRgb, look.AiStrength);
            owned = true;
        }

        if (IsColorKind(input.Kind))
        {
            // 2. Sharpen.
            if (look.Sharpen is { Amount: > 0 } sharpen)
            {
                rgb = Writable(rgb, ref owned);
                ImageOps.UnsharpMask(rgb, sharpen.Amount, sharpen.Radius);
            }

            // 3. Colour grading.
            if (HasColorGrading(look))
            {
                rgb = Writable(rgb, ref owned);
                ImageOps.ColorGrade(rgb, look.Brightness, look.Contrast, look.Gamma, look.Saturation, look.Temperature);
            }

            // TODO(denoise): look.Denoise is persisted but not rendered; it will pick a denoise model variant once one is published.
        }

        // 4. Normal strength (the renormalisation always runs for normal maps, as in M2).
        if (input.Kind == TextureKind.Normal)
        {
            rgb = Writable(rgb, ref owned);
            ImageOps.RenormalizeNormals(rgb, look.NormalStrength);
        }

        // 5. Specular scale.
        PlanarImage? alpha = input.Alpha;
        if (look.SpecularScale != 1.0)
        {
            if (input.Kind == TextureKind.Specular)
            {
                rgb = Writable(rgb, ref owned);
                ImageOps.ScaleChannels(rgb, 3, look.SpecularScale);
            }
            else if (input.Kind == TextureKind.Normal && alpha is not null)
            {
                alpha = Copy(alpha);
                ImageOps.ScaleChannels(alpha, 1, look.SpecularScale);
            }
        }

        // 6. Merge alpha.
        return ImageOps.ToRgba(rgb, alpha);
    }

    private static bool HasColorGrading(LookSettings look) =>
        look.Brightness != 0.0 || look.Contrast != 1.0 || look.Gamma != 1.0 || look.Saturation != 1.0 || look.Temperature != 0.0;

    private static PlanarImage Writable(PlanarImage image, ref bool owned)
    {
        if (owned)
        {
            return image;
        }

        owned = true;
        return Copy(image);
    }

    private static PlanarImage Copy(PlanarImage image) => new(image.Channels, image.Width, image.Height, (float[])image.Data.Clone());
}
