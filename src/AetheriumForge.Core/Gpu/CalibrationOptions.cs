namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Options for <see cref="GpuProfiler.CalibrateAsync"/> (IMPLEMENTATION_GUIDE §9, "GPU auto profile").
/// </summary>
public sealed record CalibrationOptions
{
    /// <summary>Gets the soft time budget for the whole calibration. Tile growth and timed runs stop early to respect it. Default 30 s.</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the number of untimed warm-up runs before each benchmark (shader compilation, allocator warm-up). Default 2.</summary>
    public int WarmupRuns { get; init; } = 2;

    /// <summary>Gets the number of timed runs per benchmark; at least one always runs, the rest only while time remains. Default 3.</summary>
    public int TimedRuns { get; init; } = 3;

    /// <summary>Gets the fraction of the available video memory the calibration may use. Default 0.8.</summary>
    public double BudgetFraction { get; init; } = 0.8;

    /// <summary>Gets the smallest tile size in source pixels. Default 64.</summary>
    public int MinTileSize { get; init; } = 64;

    /// <summary>Gets the largest tile size in source pixels. Default 1024.</summary>
    public int MaxTileSize { get; init; } = 1024;

    /// <summary>Gets the tile overlap the upscaler adds on each side; benchmark inputs are <c>tile + 2 × overlap</c> square. Default 24.</summary>
    public int Overlap { get; init; } = 24;

    /// <summary>
    /// Gets the tile size of the reference benchmark that compares FP16 with FP32 (or times the only variant), capped by the budget estimate.
    /// Default 256.
    /// </summary>
    public int PrecisionTileSize { get; init; } = 256;

    /// <summary>
    /// Gets the relative speed-up FP16 needs over FP32 to be chosen; FP32 is the safer default. Default 0.05 (5 %).
    /// </summary>
    public double Fp16MinimumSpeedup { get; init; } = 0.05;

    /// <summary>
    /// Gets the assumed video memory per model input pixel used for the first tile estimate, before any measurement. Default 16 KiB.
    /// </summary>
    public long EstimatedBytesPerInputPixel { get; init; } = 16 * 1024;

    /// <summary>
    /// Gets how many times slower per input pixel a tile's first run may be than the reference benchmark (the precision step at
    /// <see cref="PrecisionTileSize"/>) before the tile counts as too big. It catches drivers that page video memory out to shared
    /// memory instead of failing, which on a GTX 1080 makes <c>realesrgan-x4plus</c> three times slower above a 448 tile. The margin
    /// covers the shader compilation in a first run. Default 2.
    /// </summary>
    public double MaxProbeSlowdown { get; init; } = 2.0;

    /// <summary>Gets how often video memory is sampled during a probe run to find its peak usage. Default 5 ms.</summary>
    public TimeSpan MemorySampleInterval { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Gets the maximum number of tile sizes tried while searching. Default 8.</summary>
    public int MaxTileProbes { get; init; } = 8;

    /// <summary>Gets a value indicating whether runners use DirectML (<see langword="true"/>) or the CPU. Default <see langword="true"/>.</summary>
    public bool UseDirectML { get; init; } = true;

    /// <summary>
    /// Throws when a value is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TimeBudget, TimeSpan.Zero, nameof(TimeBudget));
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupRuns, nameof(WarmupRuns));
        ArgumentOutOfRangeException.ThrowIfLessThan(TimedRuns, 1, nameof(TimedRuns));
        if (BudgetFraction is <= 0 or > 1 || double.IsNaN(BudgetFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(BudgetFraction), BudgetFraction, "The budget fraction must be in (0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MinTileSize, 16, nameof(MinTileSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTileSize, MinTileSize, nameof(MaxTileSize));
        ArgumentOutOfRangeException.ThrowIfNegative(Overlap, nameof(Overlap));
        ArgumentOutOfRangeException.ThrowIfLessThan(PrecisionTileSize, MinTileSize, nameof(PrecisionTileSize));
        if (Fp16MinimumSpeedup < 0 || double.IsNaN(Fp16MinimumSpeedup))
        {
            throw new ArgumentOutOfRangeException(nameof(Fp16MinimumSpeedup), Fp16MinimumSpeedup, "The FP16 speed-up must not be negative.");
        }

        if (MaxProbeSlowdown < 1 || double.IsNaN(MaxProbeSlowdown))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProbeSlowdown), MaxProbeSlowdown, "The probe slowdown limit must be at least 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(EstimatedBytesPerInputPixel, 1, nameof(EstimatedBytesPerInputPixel));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTileProbes, 1, nameof(MaxTileProbes));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MemorySampleInterval, TimeSpan.Zero, nameof(MemorySampleInterval));
    }
}

/// <summary>
/// Calibration progress reported by <see cref="GpuProfiler.CalibrateAsync"/>.
/// </summary>
/// <param name="Step">The current step: <see cref="GpuProfiler.PrecisionStep"/>, <see cref="GpuProfiler.TileStep"/> or <see cref="GpuProfiler.ThroughputStep"/>.</param>
/// <param name="Done">Work items finished in this step.</param>
/// <param name="Total">Work items planned for this step (an upper bound for the tile search).</param>
public readonly record struct CalibrationProgress(string Step, int Done, int Total);
