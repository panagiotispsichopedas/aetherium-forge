using System.Numerics;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Pixel post-processing for one texture (IMPLEMENTATION_GUIDE §10 steps 3, 5, 6 and 7): channel split and merge, resizing and
/// normal-map renormalisation. The maths follows the reference prototype (<c>run.py</c>, Pillow + NumPy) but works in float.
/// </summary>
public static class ImageOps
{
    private const float ByteToUnit = 1f / 255f;
    private const float MinimumNormalZ = 0.05f;

    /// <summary>
    /// Converts the RGB channels of an RGBA8 image to a three-channel planar image with values 0–1.
    /// </summary>
    /// <param name="image">The source image.</param>
    /// <returns>A new planar image with 3 channels.</returns>
    public static PlanarImage ToPlanarRgb(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int width = image.Width;
        int plane = width * image.Height;
        var result = new PlanarImage(3, width, image.Height);
        byte[] pixels = image.Pixels;
        float[] data = result.Data;

        Parallel.For(0, image.Height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                int s = p * 4;
                data[p] = pixels[s] * ByteToUnit;
                data[plane + p] = pixels[s + 1] * ByteToUnit;
                data[(2 * plane) + p] = pixels[s + 2] * ByteToUnit;
            }
        });

        return result;
    }

    /// <summary>
    /// Extracts the alpha channel as a one-channel planar image, but only when it carries information.
    /// </summary>
    /// <param name="image">The source image.</param>
    /// <returns>
    /// The alpha plane with values 0–1, or <see langword="null"/> when every alpha value is 255 (the guide keeps alpha only when
    /// its minimum is below 255).
    /// </returns>
    public static PlanarImage? ExtractAlpha(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] pixels = image.Pixels;
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            return null;
        }

        var result = new PlanarImage(1, image.Width, image.Height);
        float[] data = result.Data;
        for (int p = 0; p < data.Length; p++)
        {
            data[p] = pixels[(p * 4) + 3] * ByteToUnit;
        }

        return result;
    }

    /// <summary>
    /// Resizes every channel with a separable filter, using Pillow's resampling semantics: Lanczos a = 3 or bicubic a = -0.5,
    /// sample centres at <c>(i + 0.5) × scale</c>, filter support widened by the scale factor when downscaling (antialiasing),
    /// and the kernel window clamped to the image edge with renormalised weights.
    /// </summary>
    /// <remarks>
    /// A dimension that does not change is not resampled, so resizing to the same size is an exact copy. The horizontal pass runs
    /// first; each pass clamps its output to 0–1 (as Pillow clips each pass of an 8-bit image), so ringing never leaves the range.
    /// </remarks>
    /// <param name="source">The source image.</param>
    /// <param name="width">Target width; at least 1.</param>
    /// <param name="height">Target height; at least 1.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>A new image of the target size with the same channel count.</returns>
    public static PlanarImage Resize(PlanarImage source, int width, int height, ResizeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown resize filter.");
        }

        PlanarImage current = source;
        if (width != source.Width)
        {
            current = ResizeHorizontal(current, width, ResampleKernel.Create(source.Width, width, filter));
        }

        if (height != source.Height)
        {
            current = ResizeVertical(current, height, ResampleKernel.Create(source.Height, height, filter));
        }

        return ReferenceEquals(current, source) ? new PlanarImage(source.Channels, source.Width, source.Height, (float[])source.Data.Clone()) : current;
    }

    /// <summary>
    /// Renormalises a tangent-space normal map in place, as the prototype does: <c>v = 2c − 1</c>, <c>v.xy ×= strength</c>,
    /// <c>v.z = max(v.z, 0.05)</c>, <c>v = v / |v|</c>, <c>c = (v + 1) / 2</c>.
    /// </summary>
    /// <remarks>Only the first three channels are touched. Strength 1 leaves valid unit normals (with z ≥ 0.05) unchanged.</remarks>
    /// <param name="rgb">The normal map, at least 3 channels with values 0–1.</param>
    /// <param name="strength">The XY multiplier; above 1 deepens surface relief. Must be finite and not negative.</param>
    public static void RenormalizeNormals(PlanarImage rgb, double strength)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Channels < 3)
        {
            throw new ArgumentException($"A normal map needs at least 3 channels, got {rgb.Channels}.", nameof(rgb));
        }

        if (!double.IsFinite(strength) || strength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "The normal strength must be finite and not negative.");
        }

        int width = rgb.Width;
        int plane = width * rgb.Height;
        float[] data = rgb.Data;
        float s = (float)strength;

        Parallel.For(0, rgb.Height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                float vx = ((2f * data[p]) - 1f) * s;
                float vy = ((2f * data[plane + p]) - 1f) * s;
                float vz = MathF.Max((2f * data[(2 * plane) + p]) - 1f, MinimumNormalZ);
                float inverseLength = 1f / MathF.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
                data[p] = Math.Clamp(((vx * inverseLength) + 1f) * 0.5f, 0f, 1f);
                data[plane + p] = Math.Clamp(((vy * inverseLength) + 1f) * 0.5f, 0f, 1f);
                data[(2 * plane) + p] = Math.Clamp(((vz * inverseLength) + 1f) * 0.5f, 0f, 1f);
            }
        });
    }

    /// <summary>
    /// Converts a planar RGB image (and optional alpha plane) back to RGBA8, rounding to nearest and clamping to 0–255.
    /// </summary>
    /// <param name="rgb">The colour image, at least 3 channels; only the first three are used.</param>
    /// <param name="alpha">The alpha plane (first channel used), or <see langword="null"/> for opaque (255).</param>
    /// <returns>A new RGBA8 image.</returns>
    /// <exception cref="ArgumentException">The channel count is too low or the sizes differ.</exception>
    public static RgbaImage ToRgba(PlanarImage rgb, PlanarImage? alpha)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Channels < 3)
        {
            throw new ArgumentException($"Expected at least 3 colour channels, got {rgb.Channels}.", nameof(rgb));
        }

        if (alpha is not null && (alpha.Width != rgb.Width || alpha.Height != rgb.Height))
        {
            throw new ArgumentException($"Alpha is {alpha.Width}×{alpha.Height} but colour is {rgb.Width}×{rgb.Height}.", nameof(alpha));
        }

        int width = rgb.Width;
        int plane = width * rgb.Height;
        var result = new RgbaImage(width, rgb.Height);
        byte[] pixels = result.Pixels;
        float[] colour = rgb.Data;
        float[]? alphaData = alpha?.Data;

        Parallel.For(0, rgb.Height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                int t = p * 4;
                pixels[t] = ToByte(colour[p]);
                pixels[t + 1] = ToByte(colour[plane + p]);
                pixels[t + 2] = ToByte(colour[(2 * plane) + p]);
                pixels[t + 3] = alphaData is null ? (byte)255 : ToByte(alphaData[p]);
            }
        });

        return result;
    }

    private static byte ToByte(float value)
    {
        // NaN and negatives map to 0; the comparison form keeps NaN out of the conversion.
        if (!(value > 0f))
        {
            return 0;
        }

        return value >= 1f ? (byte)255 : (byte)((value * 255f) + 0.5f);
    }

    private static PlanarImage ResizeHorizontal(PlanarImage source, int width, ResampleKernel kernel)
    {
        int inWidth = source.Width;
        int height = source.Height;
        var result = new PlanarImage(source.Channels, width, height);
        float[] input = source.Data;
        float[] output = result.Data;
        int taps = kernel.Taps;
        int[] starts = kernel.Starts;
        int[] counts = kernel.Counts;
        float[] weights = kernel.Weights;

        Parallel.For(0, source.Channels * height, row =>
        {
            ReadOnlySpan<float> inRow = input.AsSpan(row * inWidth, inWidth);
            Span<float> outRow = output.AsSpan(row * width, width);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<float> window = inRow.Slice(starts[x], counts[x]);
                ReadOnlySpan<float> w = weights.AsSpan(x * taps, counts[x]);
                float sum = 0f;
                for (int k = 0; k < window.Length; k++)
                {
                    sum += window[k] * w[k];
                }

                outRow[x] = Math.Clamp(sum, 0f, 1f);
            }
        });

        return result;
    }

    private static PlanarImage ResizeVertical(PlanarImage source, int height, ResampleKernel kernel)
    {
        int width = source.Width;
        int inHeight = source.Height;
        var result = new PlanarImage(source.Channels, width, height);
        float[] input = source.Data;
        float[] output = result.Data;
        int taps = kernel.Taps;
        int[] starts = kernel.Starts;
        int[] counts = kernel.Counts;
        float[] weights = kernel.Weights;
        int vectorWidth = Vector<float>.Count;
        var zero = Vector<float>.Zero;
        var one = Vector<float>.One;

        Parallel.For(0, source.Channels * height, row =>
        {
            int channel = row / height;
            int y = row - (channel * height);
            int inPlane = channel * inHeight * width;
            Span<float> outRow = output.AsSpan(row * width, width);
            outRow.Clear();

            int count = counts[y];
            int weightOffset = y * taps;
            for (int k = 0; k < count; k++)
            {
                float weight = weights[weightOffset + k];
                var weightVector = new Vector<float>(weight);
                ReadOnlySpan<float> inRow = input.AsSpan(inPlane + ((starts[y] + k) * width), width);
                int x = 0;
                for (; x <= width - vectorWidth; x += vectorWidth)
                {
                    var accumulated = new Vector<float>(outRow[x..]) + (new Vector<float>(inRow[x..]) * weightVector);
                    accumulated.CopyTo(outRow[x..]);
                }

                for (; x < width; x++)
                {
                    outRow[x] += inRow[x] * weight;
                }
            }

            int i = 0;
            for (; i <= width - vectorWidth; i += vectorWidth)
            {
                Vector.Min(Vector.Max(new Vector<float>(outRow[i..]), zero), one).CopyTo(outRow[i..]);
            }

            for (; i < width; i++)
            {
                outRow[i] = Math.Clamp(outRow[i], 0f, 1f);
            }
        });

        return result;
    }
}
