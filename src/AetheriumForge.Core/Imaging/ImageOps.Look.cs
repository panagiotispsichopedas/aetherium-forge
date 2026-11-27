using System.Numerics;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Look operations (IMPLEMENTATION_GUIDE §8): AI-strength blend, unsharp mask, colour grading and channel scaling. Each works on
/// planar 0–1 data, runs rows in parallel and clamps its output to 0–1.
/// </summary>
public static partial class ImageOps
{
    /// <summary>Luminance-preserving R and B gain per unit of temperature (±1 → R ×1.1 / B ×0.9 before the luminance correction).</summary>
    internal const float TemperatureGain = 0.1f;

    /// <summary>The linear mid-grey that contrast pivots around (18 % reflectance).</summary>
    internal const float LinearMidGrey = 0.18f;

    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;
    private const int DecodeLutSize = 4096;
    private const int EncodeLutSize = 65536;

    private static readonly float[] SrgbToLinearLut = BuildLut(DecodeLutSize, SrgbToLinear);
    private static readonly float[] LinearToSrgbLut = BuildLut(EncodeLutSize, LinearToSrgb);

    /// <summary>
    /// Blends two images of the same size: <c>result = baseline + strength × (detail − baseline)</c>, clamped to 0–1.
    /// </summary>
    /// <param name="detail">The image weighted by <paramref name="strength"/> (the AI output).</param>
    /// <param name="baseline">The image weighted by <c>1 − strength</c> (the Lanczos upscale).</param>
    /// <param name="strength">The weight of <paramref name="detail"/>, 0–1.</param>
    /// <returns>A new image.</returns>
    /// <exception cref="ArgumentException">The images differ in size or channel count.</exception>
    public static PlanarImage Blend(PlanarImage detail, PlanarImage baseline, double strength)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(baseline);
        if (detail.Channels != baseline.Channels || detail.Width != baseline.Width || detail.Height != baseline.Height)
        {
            throw new ArgumentException(
                $"Cannot blend {detail.Channels}×{detail.Width}×{detail.Height} with {baseline.Channels}×{baseline.Width}×{baseline.Height}.",
                nameof(baseline));
        }

        var result = new PlanarImage(detail.Channels, detail.Width, detail.Height);
        float[] a = detail.Data;
        float[] b = baseline.Data;
        float[] output = result.Data;
        int width = detail.Width;
        var weight = new Vector<float>((float)Math.Clamp(strength, 0.0, 1.0));
        float scalarWeight = weight[0];
        int vectorWidth = Vector<float>.Count;

        Parallel.For(0, detail.Channels * detail.Height, row =>
        {
            int start = row * width;
            ReadOnlySpan<float> aRow = a.AsSpan(start, width);
            ReadOnlySpan<float> bRow = b.AsSpan(start, width);
            Span<float> outRow = output.AsSpan(start, width);
            int x = 0;
            for (; x <= width - vectorWidth; x += vectorWidth)
            {
                var baseValue = new Vector<float>(bRow[x..]);
                var blended = baseValue + ((new Vector<float>(aRow[x..]) - baseValue) * weight);
                Vector.Min(Vector.Max(blended, Vector<float>.Zero), Vector<float>.One).CopyTo(outRow[x..]);
            }

            for (; x < width; x++)
            {
                outRow[x] = Math.Clamp(bRow[x] + ((aRow[x] - bRow[x]) * scalarWeight), 0f, 1f);
            }
        });

        return result;
    }

    /// <summary>
    /// Sharpens every channel in place with an unsharp mask: <c>v + amount × (v − gaussian(v))</c>, clamped to 0–1. The Gaussian
    /// is separable with sigma <paramref name="radius"/>, a support of ⌈3σ⌉ pixels each side, and edge pixels repeated.
    /// </summary>
    /// <param name="image">The image to sharpen.</param>
    /// <param name="amount">The strength; 0 (or less) leaves the image unchanged.</param>
    /// <param name="radius">The Gaussian sigma in pixels; must be positive and finite.</param>
    public static void UnsharpMask(PlanarImage image, double amount, double radius)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The sharpen radius must be positive and finite.");
        }

        if (!(amount > 0) || !double.IsFinite(amount))
        {
            return;
        }

        float[] kernel = GaussianKernel(radius);
        int half = kernel.Length / 2;
        int width = image.Width;
        int height = image.Height;
        float[] data = image.Data;
        float[] horizontal = new float[data.Length];
        float strength = (float)amount;

        // Horizontal pass into a scratch buffer.
        Parallel.For(0, image.Channels * height, row =>
        {
            ReadOnlySpan<float> inRow = data.AsSpan(row * width, width);
            Span<float> outRow = horizontal.AsSpan(row * width, width);
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;
                if (x >= half && x < width - half)
                {
                    ReadOnlySpan<float> window = inRow.Slice(x - half, kernel.Length);
                    for (int k = 0; k < kernel.Length; k++)
                    {
                        sum += window[k] * kernel[k];
                    }
                }
                else
                {
                    for (int k = 0; k < kernel.Length; k++)
                    {
                        sum += inRow[Math.Clamp(x + k - half, 0, width - 1)] * kernel[k];
                    }
                }

                outRow[x] = sum;
            }
        });

        // Vertical pass, combined with the unsharp step; each output row reads only its own source row, so it can be written in place.
        int vectorWidth = Vector<float>.Count;
        var strengthVector = new Vector<float>(strength);
        Parallel.For(0, image.Channels * height, row =>
        {
            int channel = row / height;
            int y = row - (channel * height);
            int plane = channel * height * width;
            Span<float> blurred = width <= 1024 ? stackalloc float[width] : new float[width];
            blurred.Clear();
            for (int k = 0; k < kernel.Length; k++)
            {
                int sourceY = Math.Clamp(y + k - half, 0, height - 1);
                ReadOnlySpan<float> inRow = horizontal.AsSpan(plane + (sourceY * width), width);
                var weight = new Vector<float>(kernel[k]);
                int x = 0;
                for (; x <= width - vectorWidth; x += vectorWidth)
                {
                    (new Vector<float>(blurred[x..]) + (new Vector<float>(inRow[x..]) * weight)).CopyTo(blurred[x..]);
                }

                for (; x < width; x++)
                {
                    blurred[x] += inRow[x] * kernel[k];
                }
            }

            Span<float> target = data.AsSpan(plane + (y * width), width);
            int i = 0;
            for (; i <= width - vectorWidth; i += vectorWidth)
            {
                var value = new Vector<float>(target[i..]);
                var sharpened = value + ((value - new Vector<float>(blurred[i..])) * strengthVector);
                Vector.Min(Vector.Max(sharpened, Vector<float>.Zero), Vector<float>.One).CopyTo(target[i..]);
            }

            for (; i < width; i++)
            {
                target[i] = Math.Clamp(target[i] + ((target[i] - blurred[i]) * strength), 0f, 1f);
            }
        });
    }

    /// <summary>
    /// Colour-grades an sRGB-encoded RGB image in place. Each pixel is decoded with the sRGB EOTF to linear light, then:
    /// brightness adds an offset, contrast scales around linear mid-grey 0.18, saturation scales around Rec.709 luma, and
    /// temperature applies a warm/cool gain to R and B that keeps the luma. The result is encoded back to sRGB and gamma is applied
    /// to that perceptual value as <c>v^(1/gamma)</c>. Values are clamped to 0–1.
    /// </summary>
    /// <remarks>
    /// Gamma acts last, on the perceptual value, so it bends mid-tones the way an image editor's gamma slider does; the other
    /// controls act in linear light. The transfer functions use interpolated lookup tables (error below 1e-4).
    /// <para>The sRGB transfer function is used whatever the DXGI tag of the source says: Skyrim's colour textures are authored
    /// gamma-encoded even when tagged <c>UNORM</c> (the engine does not linearise them), so their 8-bit values are perceptual and must be
    /// decoded before grading in linear light.</para>
    /// <para>Temperature keeps the luma of each pixel even in the highlights: see <see cref="ApplyTemperature"/>.</para>
    /// </remarks>
    /// <param name="rgb">The image, at least 3 channels with values 0–1; only the first three are graded.</param>
    /// <param name="brightness">The linear offset, −1–1.</param>
    /// <param name="contrast">The contrast multiplier, positive.</param>
    /// <param name="gamma">The gamma, positive.</param>
    /// <param name="saturation">The saturation multiplier, 0 or more.</param>
    /// <param name="temperature">The temperature shift, −1 (cool) to 1 (warm).</param>
    public static void ColorGrade(PlanarImage rgb, double brightness, double contrast, double gamma, double saturation, double temperature)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Channels < 3)
        {
            throw new ArgumentException($"Colour grading needs at least 3 channels, got {rgb.Channels}.", nameof(rgb));
        }

        if (!double.IsFinite(gamma) || gamma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "The gamma must be positive and finite.");
        }

        float offset = (float)brightness;
        float contrastFactor = (float)contrast;
        float saturationFactor = (float)saturation;
        bool applySaturation = saturation != 1.0;
        bool applyTemperature = temperature != 0.0;
        float redGain = 1f + (TemperatureGain * (float)temperature);
        float blueGain = 1f - (TemperatureGain * (float)temperature);
        double inverseGamma = 1.0 / gamma;
        float[] encode = gamma == 1.0 ? LinearToSrgbLut : BuildLut(EncodeLutSize, v => Math.Pow(LinearToSrgb(v), inverseGamma));
        float[] decode = SrgbToLinearLut;

        int width = rgb.Width;
        int plane = width * rgb.Height;
        float[] data = rgb.Data;

        Parallel.For(0, rgb.Height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                float r = Lookup(decode, data[p]);
                float g = Lookup(decode, data[plane + p]);
                float b = Lookup(decode, data[(2 * plane) + p]);

                r = MathF.Max(LinearMidGrey + ((r + offset - LinearMidGrey) * contrastFactor), 0f);
                g = MathF.Max(LinearMidGrey + ((g + offset - LinearMidGrey) * contrastFactor), 0f);
                b = MathF.Max(LinearMidGrey + ((b + offset - LinearMidGrey) * contrastFactor), 0f);

                if (applySaturation)
                {
                    float luma = (LumaR * r) + (LumaG * g) + (LumaB * b);
                    r = MathF.Max(luma + ((r - luma) * saturationFactor), 0f);
                    g = MathF.Max(luma + ((g - luma) * saturationFactor), 0f);
                    b = MathF.Max(luma + ((b - luma) * saturationFactor), 0f);
                }

                if (applyTemperature)
                {
                    (r, g, b) = ApplyTemperature(r, g, b, redGain, blueGain);
                }

                data[p] = Lookup(encode, r);
                data[plane + p] = Lookup(encode, g);
                data[(2 * plane) + p] = Lookup(encode, b);
            }
        });
    }

    /// <summary>
    /// Applies the temperature gains to one linear pixel, keeping its Rec.709 luma. When the tinted pixel would push a channel above
    /// 1 (or above its own value when that is already above 1), the tint is pulled back toward the original pixel just enough to fit:
    /// both have the same luma, so their blend does too, and the later clamp to 1 cannot lose luma in the highlights (white stays white).
    /// </summary>
    /// <param name="r">Linear red.</param>
    /// <param name="g">Linear green.</param>
    /// <param name="b">Linear blue.</param>
    /// <param name="redGain">The red gain.</param>
    /// <param name="blueGain">The blue gain.</param>
    /// <returns>The tinted pixel.</returns>
    internal static (float R, float G, float B) ApplyTemperature(float r, float g, float b, float redGain, float blueGain)
    {
        float before = (LumaR * r) + (LumaG * g) + (LumaB * b);
        float tr = r * redGain;
        float tb = b * blueGain;
        float after = (LumaR * tr) + (LumaG * g) + (LumaB * tb);
        if (!(after > 0f))
        {
            return (r, g, b);
        }

        float keep = before / after;
        tr *= keep;
        float tg = g * keep;
        tb *= keep;

        float blend = MathF.Min(MathF.Min(Headroom(r, tr), Headroom(g, tg)), Headroom(b, tb));
        return (r + ((tr - r) * blend), g + ((tg - g) * blend), b + ((tb - b) * blend));
    }

    /// <summary>
    /// Multiplies the first <paramref name="channels"/> channels of an image in place by <paramref name="factor"/>, clamping to 0–1.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="channels">How many leading channels to scale; at most <see cref="PlanarImage.Channels"/>.</param>
    /// <param name="factor">The multiplier, 0 or more.</param>
    public static void ScaleChannels(PlanarImage image, int channels, double factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, image.Channels);
        if (!double.IsFinite(factor) || factor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be finite and not negative.");
        }

        int width = image.Width;
        float[] data = image.Data;
        var scale = new Vector<float>((float)factor);
        float scalar = (float)factor;
        int vectorWidth = Vector<float>.Count;

        Parallel.For(0, channels * image.Height, row =>
        {
            Span<float> span = data.AsSpan(row * width, width);
            int x = 0;
            for (; x <= width - vectorWidth; x += vectorWidth)
            {
                Vector.Min(Vector.Max(new Vector<float>(span[x..]) * scale, Vector<float>.Zero), Vector<float>.One).CopyTo(span[x..]);
            }

            for (; x < width; x++)
            {
                span[x] = Math.Clamp(span[x] * scalar, 0f, 1f);
            }
        });
    }

    /// <summary>The sRGB EOTF (IEC 61966-2-1): encoded 0–1 → linear 0–1.</summary>
    /// <param name="encoded">The encoded value.</param>
    /// <returns>The linear value.</returns>
    internal static double SrgbToLinear(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    /// <summary>The inverse sRGB EOTF: linear 0–1 → encoded 0–1.</summary>
    /// <param name="linear">The linear value.</param>
    /// <returns>The encoded value.</returns>
    internal static double LinearToSrgb(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;

    /// <summary>The largest blend 0–1 from <paramref name="original"/> toward <paramref name="tinted"/> that stays within max(1, original).</summary>
    private static float Headroom(float original, float tinted)
    {
        float limit = MathF.Max(1f, original);
        return tinted > limit ? Math.Clamp((limit - original) / (tinted - original), 0f, 1f) : 1f;
    }

    private static float[] BuildLut(int size, Func<double, double> function)
    {
        // One extra entry so interpolation at exactly 1.0 stays in bounds.
        var table = new float[size + 2];
        for (int i = 0; i <= size; i++)
        {
            table[i] = (float)function((double)i / size);
        }

        table[size + 1] = table[size];
        return table;
    }

    private static float Lookup(float[] table, float value)
    {
        int size = table.Length - 2;
        float position = Math.Clamp(value, 0f, 1f) * size;
        int index = (int)position;
        float fraction = position - index;
        return Math.Clamp(table[index] + ((table[index + 1] - table[index]) * fraction), 0f, 1f);
    }

    private static float[] GaussianKernel(double sigma)
    {
        int half = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
        var kernel = new float[(2 * half) + 1];
        double twoSigmaSquared = 2.0 * sigma * sigma;
        double sum = 0;
        for (int i = -half; i <= half; i++)
        {
            double weight = Math.Exp(-(i * i) / twoSigmaSquared);
            kernel[i + half] = (float)weight;
            sum += weight;
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] = (float)(kernel[i] / sum);
        }

        return kernel;
    }
}
