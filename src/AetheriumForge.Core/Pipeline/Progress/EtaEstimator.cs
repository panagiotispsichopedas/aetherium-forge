namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Thread-safe <see cref="IEtaEstimator"/>: an exponential moving average of seconds per source megapixel for each model
/// (IMPLEMENTATION_GUIDE §11). Model names are compared ordinally.
/// </summary>
public sealed class EtaEstimator : IEtaEstimator
{
    /// <summary>The default EMA smoothing factor.</summary>
    public const double DefaultSmoothing = 0.2;

    /// <summary>Samples are clamped only once more than this many real samples exist for a model.</summary>
    private const int ClampAfterSamples = 3;

    /// <summary>A clamped sample lies within [rate / factor, rate × factor].</summary>
    private const double OutlierFactor = 4.0;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ModelRate> _rates = new(StringComparer.Ordinal);
    private readonly double _smoothing;

    /// <summary>Initializes a new instance of the <see cref="EtaEstimator"/> class.</summary>
    /// <param name="smoothing">Weight of each new sample, in (0, 1]. Higher values follow recent jobs more closely.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="smoothing"/> is not in (0, 1].</exception>
    public EtaEstimator(double smoothing = DefaultSmoothing)
    {
        if (!(smoothing > 0 && smoothing <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(smoothing), smoothing, "Smoothing must be in (0, 1].");
        }

        _smoothing = smoothing;
    }

    /// <summary>
    /// Seeds the rate for a model. A seed is replaced outright by the first recorded sample, and is ignored once a real sample exists.
    /// A non-positive or non-finite rate is ignored.
    /// </summary>
    /// <param name="model">Model name.</param>
    /// <param name="secondsPerMegapixel">Seconds per source megapixel.</param>
    public void Seed(string model, double secondsPerMegapixel)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsPositiveFinite(secondsPerMegapixel))
        {
            return;
        }

        lock (_gate)
        {
            if (!_rates.TryGetValue(model, out var current) || current.Samples == 0)
            {
                _rates[model] = new ModelRate(secondsPerMegapixel, 0);
            }
        }
    }

    /// <summary>
    /// Records a finished job. Non-positive or non-finite input is ignored. Once more than three samples exist for the model, each new
    /// sample is clamped to [rate / 4, rate × 4], so one stalled job cannot wreck the estimate.
    /// </summary>
    /// <param name="model">Model name.</param>
    /// <param name="sourceMegapixels">Source width × height / 1,000,000.</param>
    /// <param name="elapsed">Total processing time of the job.</param>
    public void Record(string model, double sourceMegapixels, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsPositiveFinite(sourceMegapixels) || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var sample = elapsed.TotalSeconds / sourceMegapixels;
        if (!IsPositiveFinite(sample))
        {
            return;
        }

        lock (_gate)
        {
            if (!_rates.TryGetValue(model, out var current) || current.Samples == 0)
            {
                _rates[model] = new ModelRate(sample, 1);
                return;
            }

            if (current.Samples > ClampAfterSamples)
            {
                sample = Math.Clamp(sample, current.SecondsPerMegapixel / OutlierFactor, current.SecondsPerMegapixel * OutlierFactor);
            }

            var rate = current.SecondsPerMegapixel + (_smoothing * (sample - current.SecondsPerMegapixel));
            _rates[model] = new ModelRate(rate, current.Samples == int.MaxValue ? current.Samples : current.Samples + 1);
        }
    }

    /// <summary>
    /// Estimates the time to process <paramref name="remaining"/>. Entries with non-positive or non-finite megapixels are ignored, and a
    /// non-positive or non-finite <paramref name="parallelism"/> counts as 1. Nothing remaining gives <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <param name="remaining">Model and source megapixels of each remaining job.</param>
    /// <param name="parallelism">Effective speed-up from overlapping stages (1 = serial).</param>
    /// <returns>The estimate, or <see langword="null"/> when some remaining model has no rate yet.</returns>
    public TimeSpan? Estimate(IEnumerable<(string Model, double SourceMegapixels)> remaining, double parallelism = 1.0)
    {
        ArgumentNullException.ThrowIfNull(remaining);

        // Enumerate outside the lock: the sequence belongs to the caller and may be lazy.
        var megapixelsByModel = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (model, megapixels) in remaining)
        {
            if (IsPositiveFinite(megapixels))
            {
                megapixelsByModel[model] = megapixelsByModel.GetValueOrDefault(model) + megapixels;
            }
        }

        var seconds = 0.0;
        lock (_gate)
        {
            foreach (var (model, megapixels) in megapixelsByModel)
            {
                if (!_rates.TryGetValue(model, out var current))
                {
                    return null;
                }

                seconds += megapixels * current.SecondsPerMegapixel;
            }
        }

        seconds /= IsPositiveFinite(parallelism) ? parallelism : 1.0;
        return seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
    }

    /// <summary>Gets the current rate of a model, seeded or measured.</summary>
    /// <param name="model">Model name.</param>
    /// <param name="secondsPerMegapixel">Seconds per source megapixel, or 0 when unknown.</param>
    /// <returns><see langword="true"/> when the model has a rate.</returns>
    public bool TryGetRate(string model, out double secondsPerMegapixel)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            var found = _rates.TryGetValue(model, out var current);
            secondsPerMegapixel = current.SecondsPerMegapixel;
            return found;
        }
    }

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);

    private readonly record struct ModelRate(double SecondsPerMegapixel, int Samples);
}
