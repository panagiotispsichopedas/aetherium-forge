namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Precomputed one-dimensional resampling weights, built the way Pillow's <c>precompute_coeffs</c> builds them:
/// the filter is centred on <c>(i + 0.5) × scale</c>, its support is widened by the scale when downscaling (antialiasing),
/// the window is clamped to the image and the weights are renormalised to sum to 1.
/// </summary>
internal sealed class ResampleKernel
{
    private ResampleKernel(int taps, int[] starts, int[] counts, float[] weights)
    {
        Taps = taps;
        Starts = starts;
        Counts = counts;
        Weights = weights;
    }

    /// <summary>Gets the stride of <see cref="Weights"/> per output sample.</summary>
    public int Taps { get; }

    /// <summary>Gets the first source index per output sample.</summary>
    public int[] Starts { get; }

    /// <summary>Gets the number of source samples per output sample.</summary>
    public int[] Counts { get; }

    /// <summary>Gets the weights, <see cref="Taps"/> per output sample.</summary>
    public float[] Weights { get; }

    /// <summary>
    /// Builds the weights that map <paramref name="inSize"/> samples to <paramref name="outSize"/> samples.
    /// </summary>
    /// <param name="inSize">Source length.</param>
    /// <param name="outSize">Target length.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>The kernel.</returns>
    public static ResampleKernel Create(int inSize, int outSize, ResizeFilter filter)
    {
        double baseSupport = filter == ResizeFilter.Lanczos3 ? 3.0 : 2.0;
        double scale = (double)inSize / outSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = baseSupport * filterScale;
        double inverseFilterScale = 1.0 / filterScale;
        int taps = (int)Math.Ceiling(support) * 2 + 1;

        int[] starts = new int[outSize];
        int[] counts = new int[outSize];
        float[] weights = new float[checked(outSize * taps)];
        Span<double> raw = taps <= 1024 ? stackalloc double[taps] : new double[taps];

        for (int i = 0; i < outSize; i++)
        {
            double center = (i + 0.5) * scale;
            int min = Math.Max((int)(center - support + 0.5), 0);
            int max = Math.Min((int)(center + support + 0.5), inSize);
            int count = Math.Min(max - min, taps);

            double sum = 0;
            for (int k = 0; k < count; k++)
            {
                double w = Evaluate(filter, (k + min - center + 0.5) * inverseFilterScale);
                raw[k] = w;
                sum += w;
            }

            if (sum == 0)
            {
                // Degenerate window (cannot happen for these filters, kept as a guard): nearest sample.
                min = Math.Clamp((int)center, 0, inSize - 1);
                count = 1;
                raw[0] = 1;
                sum = 1;
            }

            starts[i] = min;
            counts[i] = count;
            int offset = i * taps;
            for (int k = 0; k < count; k++)
            {
                weights[offset + k] = (float)(raw[k] / sum);
            }
        }

        return new ResampleKernel(taps, starts, counts, weights);
    }

    private static double Evaluate(ResizeFilter filter, double x) => filter == ResizeFilter.Lanczos3 ? Lanczos3(x) : Bicubic(x);

    private static double Lanczos3(double x)
    {
        if (x is <= -3.0 or >= 3.0)
        {
            return 0.0;
        }

        return Sinc(x) * Sinc(x / 3.0);
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double Bicubic(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1.0)
        {
            return (((a + 2.0) * x) - (a + 3.0)) * x * x + 1.0;
        }

        if (x < 2.0)
        {
            return (((x - 5.0) * x + 8.0) * x - 4.0) * a;
        }

        return 0.0;
    }
}
