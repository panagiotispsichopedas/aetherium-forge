using AetheriumForge.Core.Imaging;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// Which channels the before/after view shows.
/// </summary>
public enum ChannelMode
{
    /// <summary>The colour channels, opaque.</summary>
    Rgb,

    /// <summary>The alpha channel as grey.</summary>
    Alpha,

    /// <summary>Normal-map XY as colour: X in red, Y in green, no blue, so the relief direction stands out.</summary>
    Normal,
}

/// <summary>
/// Converts an RGBA image into the opaque image shown for a <see cref="ChannelMode"/>.
/// </summary>
public static class ChannelView
{
    /// <summary>Builds the display image of <paramref name="image"/> for <paramref name="mode"/>.</summary>
    /// <param name="image">The image.</param>
    /// <param name="mode">The channel mode.</param>
    /// <returns>A new opaque RGBA image of the same size.</returns>
    public static RgbaImage Convert(RgbaImage image, ChannelMode mode)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = image.Pixels;
        var pixels = new byte[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            switch (mode)
            {
                case ChannelMode.Alpha:
                    pixels[i] = source[i + 3];
                    pixels[i + 1] = source[i + 3];
                    pixels[i + 2] = source[i + 3];
                    break;
                case ChannelMode.Normal:
                    pixels[i] = source[i];
                    pixels[i + 1] = source[i + 1];
                    pixels[i + 2] = 0;
                    break;
                default:
                    pixels[i] = source[i];
                    pixels[i + 1] = source[i + 1];
                    pixels[i + 2] = source[i + 2];
                    break;
            }

            pixels[i + 3] = 255;
        }

        return new RgbaImage(image.Width, image.Height, pixels);
    }
}
