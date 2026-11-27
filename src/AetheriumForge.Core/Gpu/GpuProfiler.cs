using AetheriumForge.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Builds the GPU auto profile (IMPLEMENTATION_GUIDE §9, "GPU auto profile", steps 1–6): picks the adapter, classifies its tier,
/// chooses the faster precision, finds the largest tile that fits the video-memory budget and measures the throughput.
/// </summary>
/// <remarks>
/// <para>Calibration runs synchronously on a thread-pool thread and never runs two models at once, so it must not share the adapter
/// with a running pipeline (one GPU worker per adapter, §11).</para>
/// <para>Memory is measured with <see cref="IGpuAdapterProvider.QueryVideoMemory"/> before calibration (the baseline) and after each
/// probe run. The DirectML allocator keeps its buffers after a run, so the usage after a run approximates its peak. After a tile is
/// rejected the runner is recreated, so the cached buffers of the rejected size do not count against the next, smaller size.</para>
/// </remarks>
public sealed partial class GpuProfiler
{
    /// <summary>The compact model family recommended for <see cref="GpuTier.Low"/>.</summary>
    public const string LowTierModelFamily = "realesr-general-x4v3";

    /// <summary>The model family recommended for <see cref="GpuTier.Standard"/>.</summary>
    public const string StandardTierModelFamily = "realesrgan-x4plus";

    /// <summary>Dedicated video memory below which a discrete adapter is <see cref="GpuTier.Low"/>: 4 GiB.</summary>
    public const ulong LowTierMemoryThreshold = 4UL * 1024 * 1024 * 1024;

    /// <summary>The <see cref="CalibrationProgress.Step"/> of the FP16 against FP32 benchmark.</summary>
    public const string PrecisionStep = "precision";

    /// <summary>The <see cref="CalibrationProgress.Step"/> of the tile-size search.</summary>
    public const string TileStep = "tile";

    /// <summary>The <see cref="CalibrationProgress.Step"/> of the throughput measurement.</summary>
    public const string ThroughputStep = "throughput";

    /// <summary>The precision name of FP32 models.</summary>
    public const string Fp32 = "fp32";

    /// <summary>The precision name of FP16 models.</summary>
    public const string Fp16 = "fp16";

    private const int TileGranularity = 64;

    private static readonly string[] _precisionSuffixes = ["-fp16", "_fp16", ".fp16", "-fp32", "_fp32", ".fp32"];

    private readonly IGpuAdapterProvider _adapterProvider;
    private readonly Func<ModelInfo, OnnxRunnerOptions, IModelRunner> _runnerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuProfiler"/> class.
    /// </summary>
    /// <param name="adapterProvider">Enumerates adapters and reads their video memory.</param>
    /// <param name="runnerFactory">Creates a model runner; defaults to <see cref="OnnxModelRunner.Create"/>.</param>
    /// <param name="timeProvider">Clock for benchmarks and the calibration time stamp; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public GpuProfiler(
        IGpuAdapterProvider adapterProvider,
        Func<ModelInfo, OnnxRunnerOptions, IModelRunner>? runnerFactory = null,
        TimeProvider? timeProvider = null,
        ILogger<GpuProfiler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(adapterProvider);
        _adapterProvider = adapterProvider;
        _runnerFactory = runnerFactory ?? ((model, options) => OnnxModelRunner.Create(model.Path, options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Returns the model family of a model name: the name without a precision suffix (<c>realesrgan-x4plus-fp16</c> → <c>realesrgan-x4plus</c>).
    /// </summary>
    /// <param name="modelName">The model name (<see cref="ModelInfo.Name"/>).</param>
    /// <returns>The family name.</returns>
    public static string GetModelFamily(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        foreach (var suffix in _precisionSuffixes)
        {
            if (modelName.Length > suffix.Length && modelName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return modelName[..^suffix.Length];
            }
        }

        return modelName;
    }

    /// <summary>
    /// Picks the adapter reported by the adapter provider (see <see cref="SelectAdapter(IReadOnlyList{GpuAdapterInfo})"/>).
    /// </summary>
    /// <returns>The adapter, or <see langword="null"/> when there is no usable hardware adapter.</returns>
    public GpuAdapterInfo? SelectAdapter() => SelectAdapter(_adapterProvider.GetAdapters());

    /// <summary>
    /// Picks the discrete adapter with the most dedicated video memory. Software adapters are skipped; an integrated adapter is the fallback.
    /// </summary>
    /// <param name="adapters">The adapters, for example from <see cref="IGpuAdapterProvider.GetAdapters"/>.</param>
    /// <returns>The adapter, or <see langword="null"/> when there is no usable hardware adapter.</returns>
    public GpuAdapterInfo? SelectAdapter(IReadOnlyList<GpuAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var selected = Best(adapters.Where(adapter => !adapter.IsSoftware && !adapter.IsIntegrated))
            ?? Best(adapters.Where(adapter => !adapter.IsSoftware && adapter.IsIntegrated));
        if (selected is null)
        {
            LogNoAdapter(_logger, adapters.Count);
        }
        else
        {
            LogAdapterSelected(_logger, selected.Name, selected.DirectMLDeviceId, selected.DedicatedVideoMemory / (1024 * 1024), selected.IsIntegrated);
        }

        return selected;

        static GpuAdapterInfo? Best(IEnumerable<GpuAdapterInfo> candidates) => candidates
            .OrderByDescending(adapter => adapter.DedicatedVideoMemory)
            .ThenBy(adapter => adapter.DirectMLDeviceId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Classifies an adapter: <see cref="GpuTier.Low"/> when it is integrated or has less than 4 GiB of dedicated memory.
    /// </summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>The tier.</returns>
    public GpuTier ClassifyTier(GpuAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var tier = adapter.IsIntegrated || adapter.DedicatedVideoMemory < LowTierMemoryThreshold ? GpuTier.Low : GpuTier.Standard;
        LogTier(_logger, adapter.Name, tier);
        return tier;
    }

    /// <summary>
    /// Returns the default model family for a tier: <see cref="LowTierModelFamily"/> or <see cref="StandardTierModelFamily"/>.
    /// It only seeds the first-run group defaults; the Rules screen keeps the per-group choice.
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The model family name.</returns>
    public string RecommendModelFamily(GpuTier tier)
    {
        var family = tier == GpuTier.Low ? LowTierModelFamily : StandardTierModelFamily;
        LogRecommendation(_logger, tier, family);
        return family;
    }

    /// <summary>
    /// Calibrates one model family on one adapter: precision, tile size and throughput.
    /// </summary>
    /// <param name="adapter">The adapter to calibrate.</param>
    /// <param name="modelFamilyVariants">The FP32 and optional FP16 variant of one model family (by <see cref="ModelInfo.Precision"/>).</param>
    /// <param name="options">Calibration options; defaults when <see langword="null"/>.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the calibration between model runs.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ArgumentException">The variants are empty, from different families, or have no FP32/FP16 precision.</exception>
    /// <exception cref="VideoMemoryExhaustedException">Even the smallest tile does not fit in video memory.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<GpuProfile> CalibrateAsync(
        GpuAdapterInfo adapter,
        IReadOnlyList<ModelInfo> modelFamilyVariants,
        CalibrationOptions? options = null,
        IProgress<CalibrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(modelFamilyVariants);
        options ??= new CalibrationOptions();
        options.Validate();
        if (modelFamilyVariants.Count == 0)
        {
            throw new ArgumentException("At least one model variant is required.", nameof(modelFamilyVariants));
        }

        var family = GetModelFamily(modelFamilyVariants[0].Name);
        if (modelFamilyVariants.Any(model => !string.Equals(GetModelFamily(model.Name), family, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"All variants must belong to one model family ('{family}').", nameof(modelFamilyVariants));
        }

        var fp32 = modelFamilyVariants.FirstOrDefault(model => string.Equals(model.Precision, Fp32, StringComparison.OrdinalIgnoreCase));
        var fp16 = modelFamilyVariants.FirstOrDefault(model => string.Equals(model.Precision, Fp16, StringComparison.OrdinalIgnoreCase));
        if (fp32 is null && fp16 is null)
        {
            throw new ArgumentException("No variant has precision fp32 or fp16.", nameof(modelFamilyVariants));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Calibrate(adapter, family, fp32, fp16, options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the next tile size to try in the search, or <see langword="null"/> when the search has converged.
    /// </summary>
    /// <param name="largestFit">The largest tile known to fit, if any.</param>
    /// <param name="smallestFailure">The smallest tile known not to fit, if any.</param>
    /// <param name="minTileSize">The smallest allowed tile.</param>
    /// <param name="maxTileSize">The largest allowed tile.</param>
    /// <returns>The next tile size, or <see langword="null"/>.</returns>
    internal static int? NextTileSize(int? largestFit, int? smallestFailure, int minTileSize, int maxTileSize)
    {
        if (largestFit is not { } fit)
        {
            var failed = smallestFailure ?? throw new ArgumentException("Either a fit or a failure is required.", nameof(smallestFailure));
            return failed <= minTileSize ? null : Math.Max(minTileSize, AlignDown(failed / 2, minTileSize));
        }

        if (smallestFailure is not { } failure)
        {
            return fit >= maxTileSize ? null : Math.Min(maxTileSize, fit * 2);
        }

        var middle = AlignDown((fit + failure) / 2, minTileSize);
        return middle > fit && middle < failure ? middle : null;
    }

    /// <summary>
    /// Estimates the first tile size from the memory allowance, assuming <see cref="CalibrationOptions.EstimatedBytesPerInputPixel"/>.
    /// </summary>
    /// <param name="allowanceBytes">Bytes the calibration may use.</param>
    /// <param name="options">The options.</param>
    /// <returns>A tile size within the option bounds, a multiple of 64 when possible.</returns>
    internal static int EstimateTileSize(ulong allowanceBytes, CalibrationOptions options)
    {
        var inputPixels = allowanceBytes / (ulong)options.EstimatedBytesPerInputPixel;
        var side = Math.Sqrt(inputPixels);
        var core = side - (2.0 * options.Overlap);
        var tile = core >= options.MaxTileSize ? options.MaxTileSize : (int)Math.Max(0, core);
        return Math.Clamp(AlignDown(tile, options.MinTileSize), options.MinTileSize, options.MaxTileSize);
    }

    private static int AlignDown(int value, int minimum) => value >= TileGranularity ? value / TileGranularity * TileGranularity : Math.Max(value, minimum);

    private static PlanarImage CreateInput(int size)
    {
        var input = new PlanarImage(3, size, size);
        for (var i = 0; i < input.Data.Length; i++)
        {
            input.Data[i] = (i * 37 % 251) / 250f;
        }

        return input;
    }

    private static ulong FallbackMemory(GpuAdapterInfo adapter) =>
        adapter.IsIntegrated || adapter.DedicatedVideoMemory == 0 ? Math.Max(adapter.DedicatedVideoMemory, adapter.SharedSystemMemory) : adapter.DedicatedVideoMemory;

    private GpuProfile Calibrate(
        GpuAdapterInfo adapter,
        string family,
        ModelInfo? fp32,
        ModelInfo? fp16,
        CalibrationOptions options,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var baseline = _adapterProvider.QueryVideoMemory(adapter.Luid);
        var available = baseline?.Available ?? FallbackMemory(adapter);
        var allowance = (ulong)(available * options.BudgetFraction);
        var run = new CalibrationRun(
            adapter,
            options,
            new OnnxRunnerOptions { DeviceId = adapter.DirectMLDeviceId, UseDirectML = options.UseDirectML },
            progress,
            _timeProvider.GetTimestamp(),
            baseline?.CurrentUsage,
            allowance,
            cancellationToken);
        var estimate = EstimateTileSize(allowance, options);
        LogCalibrationStarted(_logger, family, adapter.Name, available / (1024 * 1024), allowance / (1024 * 1024), estimate, baseline is not null);

        var (model, referenceSecondsPerPixel) = ChoosePrecision(run, fp32, fp16, estimate);
        var precision = string.Equals(model.Precision, Fp16, StringComparison.OrdinalIgnoreCase) ? Fp16 : Fp32;

        var (runner, tileSize, warm) = FindTileSize(run, model, estimate, referenceSecondsPerPixel);
        double secondsPerTile;
        using (runner)
        {
            var warmups = warm ? Math.Max(0, options.WarmupRuns - 1) : options.WarmupRuns;
            var input = CreateInput(tileSize + (2 * options.Overlap));
            secondsPerTile = Benchmark(run, runner, input, warmups, ThroughputStep);
        }

        var secondsPerMegapixel = secondsPerTile / ((double)tileSize * tileSize / 1_000_000d);
        var tier = ClassifyTier(adapter);
        var profile = new GpuProfile(
            adapter.Luid,
            adapter.Name,
            adapter.DriverVersion,
            adapter.DirectMLDeviceId,
            family,
            precision,
            tileSize,
            secondsPerMegapixel,
            tier,
            RecommendModelFamily(tier),
            _timeProvider.GetUtcNow())
        {
            VendorId = adapter.VendorId,
            DeviceId = adapter.DeviceId,
        };

        var totalSeconds = run.Elapsed(_timeProvider).TotalSeconds;
        LogCalibrated(_logger, family, adapter.Name, precision, tileSize, secondsPerMegapixel, totalSeconds);
        return profile;
    }

    private (ModelInfo Model, double SecondsPerInputPixel) ChoosePrecision(CalibrationRun run, ModelInfo? fp32, ModelInfo? fp16, int estimate)
    {
        var options = run.Options;
        var tile = Math.Clamp(Math.Min(options.PrecisionTileSize, estimate), options.MinTileSize, options.MaxTileSize);
        while (true)
        {
            try
            {
                var side = tile + (2 * options.Overlap);
                var pixels = (double)side * side;
                var input = CreateInput(side);
                if (fp32 is null || fp16 is null)
                {
                    var only = fp32 ?? fp16!;
                    var seconds = BenchmarkModel(run, only, input);
                    LogReference(_logger, tile, only.Precision, seconds * 1000);
                    return (only, seconds / pixels);
                }

                var fp32Seconds = BenchmarkModel(run, fp32, input);
                double fp16Seconds;
                try
                {
                    fp16Seconds = BenchmarkModel(run, fp16, input);
                }
                catch (Exception exception) when (exception is OnnxRuntimeException or InvalidDataException && !VideoMemoryErrors.IsOutOfMemory(exception))
                {
                    LogFp16Unusable(_logger, exception, fp16.Name);
                    return (fp32, fp32Seconds / pixels);
                }

                var useFp16 = fp16Seconds * (1 + options.Fp16MinimumSpeedup) < fp32Seconds;
                LogPrecisionChosen(_logger, tile, fp32Seconds * 1000, fp16Seconds * 1000, useFp16 ? Fp16 : Fp32);
                return useFp16 ? (fp16, fp16Seconds / pixels) : (fp32, fp32Seconds / pixels);
            }
            catch (Exception exception) when (VideoMemoryErrors.IsOutOfMemory(exception))
            {
                if (tile <= options.MinTileSize)
                {
                    throw new VideoMemoryExhaustedException($"Even a {options.MinTileSize}-pixel tile does not fit in video memory on {run.Adapter.Name}.", exception);
                }

                var smaller = Math.Max(options.MinTileSize, AlignDown(tile / 2, options.MinTileSize));
                LogPrecisionOutOfMemory(_logger, tile, smaller);
                tile = smaller;
            }
        }
    }

    private double BenchmarkModel(CalibrationRun run, ModelInfo model, PlanarImage input)
    {
        run.CancellationToken.ThrowIfCancellationRequested();
        using var runner = _runnerFactory(model, run.RunnerOptions);
        return Benchmark(run, runner, input, run.Options.WarmupRuns, PrecisionStep);
    }

    private double Benchmark(CalibrationRun run, IModelRunner runner, PlanarImage input, int warmups, string step)
    {
        var options = run.Options;
        var total = warmups + options.TimedRuns;
        var done = 0;
        for (var i = 0; i < warmups; i++)
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            runner.Run(input);
            run.Report(step, ++done, total);
        }

        var elapsed = TimeSpan.Zero;
        var count = 0;
        for (var i = 0; i < options.TimedRuns; i++)
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            if (count > 0 && run.Elapsed(_timeProvider) + (elapsed / count) > options.TimeBudget)
            {
                LogTimedRunsCut(_logger, step, count, options.TimedRuns);
                break;
            }

            var started = _timeProvider.GetTimestamp();
            runner.Run(input);
            elapsed += _timeProvider.GetElapsedTime(started);
            count++;
            run.Report(step, ++done, total);
        }

        return elapsed.TotalSeconds / count;
    }

    private (IModelRunner Runner, int TileSize, bool Warm) FindTileSize(CalibrationRun run, ModelInfo model, int estimate, double referenceSecondsPerPixel)
    {
        var options = run.Options;
        int? largestFit = null;
        int? smallestFailure = null;
        var fitDuration = TimeSpan.Zero;
        var warm = false;
        var tile = estimate;
        IModelRunner? runner = null;
        try
        {
            for (var probe = 1; ; probe++)
            {
                run.CancellationToken.ThrowIfCancellationRequested();
                runner ??= _runnerFactory(model, run.RunnerOptions);
                var (fits, duration) = Probe(run, runner, tile);
                if (fits && IsTooSlow(run, tile, duration, referenceSecondsPerPixel))
                {
                    fits = false;
                }

                if (fits)
                {
                    largestFit = tile;
                    fitDuration = duration;
                    warm = true;
                }
                else
                {
                    smallestFailure = tile;
                    runner.Dispose();
                    runner = null;
                    warm = false;
                }

                run.Report(TileStep, probe, options.MaxTileProbes);
                if (NextTileSize(largestFit, smallestFailure, options.MinTileSize, options.MaxTileSize) is not { } next)
                {
                    break;
                }

                if (largestFit is { } fit)
                {
                    if (probe >= options.MaxTileProbes)
                    {
                        break;
                    }

                    // Growing costs the probe plus the throughput runs (warm-ups after the probe and at least one timed run) at the
                    // next size, each about duration × (input area ratio).
                    var ratio = Math.Pow((next + (2.0 * options.Overlap)) / (fit + (2.0 * options.Overlap)), 2);
                    var predicted = fitDuration * ratio * (Math.Max(1, options.WarmupRuns) + 1);
                    if (run.Elapsed(_timeProvider) + predicted > options.TimeBudget)
                    {
                        LogTileSearchOutOfTime(_logger, fit, next);
                        break;
                    }
                }

                tile = next;
            }

            if (largestFit is not { } chosen)
            {
                throw new VideoMemoryExhaustedException($"Even a {options.MinTileSize}-pixel tile does not fit in video memory on {run.Adapter.Name}.");
            }

            if (runner is null)
            {
                runner = _runnerFactory(model, run.RunnerOptions);
                warm = false;
            }

            LogTileChosen(_logger, chosen, smallestFailure ?? 0);
            return (runner, chosen, warm);
        }
        catch
        {
            runner?.Dispose();
            throw;
        }
    }

    private bool IsTooSlow(CalibrationRun run, int tile, TimeSpan duration, double referenceSecondsPerPixel)
    {
        // Without a memory reading (or when the driver pages video memory out to shared memory instead of failing), a tile that is too
        // big shows up as a run that is several times slower per pixel than the small reference benchmark.
        var side = tile + (2.0 * run.Options.Overlap);
        var slowdown = duration.TotalSeconds / (side * side) / referenceSecondsPerPixel;
        if (referenceSecondsPerPixel <= 0 || double.IsNaN(slowdown) || slowdown <= run.Options.MaxProbeSlowdown)
        {
            return false;
        }

        LogProbeTooSlow(_logger, tile, slowdown, run.Options.MaxProbeSlowdown);
        return true;
    }

    private (bool Fits, TimeSpan Duration) Probe(CalibrationRun run, IModelRunner runner, int tile)
    {
        var input = CreateInput(tile + (2 * run.Options.Overlap));
        var before = _adapterProvider.QueryVideoMemory(run.Adapter.Luid);
        var started = _timeProvider.GetTimestamp();
        try
        {
            runner.Run(input);
        }
        catch (Exception exception) when (VideoMemoryErrors.IsOutOfMemory(exception))
        {
            LogProbeOutOfMemory(_logger, tile, exception.Message);
            return (false, _timeProvider.GetElapsedTime(started));
        }

        var duration = _timeProvider.GetElapsedTime(started);
        var after = _adapterProvider.QueryVideoMemory(run.Adapter.Luid);
        if (after is null || run.BaselineUsage is not { } baseline)
        {
            LogProbeUnmeasured(_logger, tile, duration.TotalMilliseconds);
            return (true, duration);
        }

        var used = after.CurrentUsage > baseline ? after.CurrentUsage - baseline : 0;
        var delta = before is not null && after.CurrentUsage > before.CurrentUsage ? after.CurrentUsage - before.CurrentUsage : 0;
        var fits = used <= run.AllowanceBytes;
        LogProbe(_logger, tile, used / (1024 * 1024), delta / (1024 * 1024), run.AllowanceBytes / (1024 * 1024), duration.TotalMilliseconds, fits);
        return (fits, duration);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "No usable GPU adapter among {Count} reported adapters")]
    private static partial void LogNoAdapter(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Selected GPU adapter {Name} (device {DeviceId}, {DedicatedMiB} MiB dedicated, integrated {Integrated})")]
    private static partial void LogAdapterSelected(ILogger logger, string name, int deviceId, ulong dedicatedMiB, bool integrated);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "GPU adapter {Name} is tier {Tier}")]
    private static partial void LogTier(ILogger logger, string name, GpuTier tier);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Tier {Tier} recommends model family {Family}")]
    private static partial void LogRecommendation(ILogger logger, GpuTier tier, string family);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Calibrating {Family} on {Adapter}: {AvailableMiB} MiB available, {AllowanceMiB} MiB allowed, first tile {Tile}, memory measured {Measured}")]
    private static partial void LogCalibrationStarted(ILogger logger, string family, string adapter, ulong availableMiB, ulong allowanceMiB, int tile, bool measured);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Precision benchmark at tile {Tile}: fp32 {Fp32Ms:F1} ms, fp16 {Fp16Ms:F1} ms; chose {Precision}")]
    private static partial void LogPrecisionChosen(ILogger logger, int tile, double fp32Ms, double fp16Ms, string precision);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "FP16 model {Model} failed to run; keeping FP32")]
    private static partial void LogFp16Unusable(ILogger logger, Exception exception, string model);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Out of video memory in the precision benchmark at tile {Tile}; retrying at {Smaller}")]
    private static partial void LogPrecisionOutOfMemory(ILogger logger, int tile, int smaller);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Tile {Tile}: {UsedMiB} MiB used above baseline (+{DeltaMiB} MiB this run), allowance {AllowanceMiB} MiB, {Ms:F0} ms, fits {Fits}")]
    private static partial void LogProbe(ILogger logger, int tile, ulong usedMiB, ulong deltaMiB, ulong allowanceMiB, double ms, bool fits);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Tile {Tile}: out of video memory ({Reason})")]
    private static partial void LogProbeOutOfMemory(ILogger logger, int tile, string reason);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Tile {Tile}: ran in {Ms:F0} ms; video memory not reported, only out-of-memory limits the tile")]
    private static partial void LogProbeUnmeasured(ILogger logger, int tile, double ms);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Tile search stops at {Tile}: trying {Next} would exceed the time budget")]
    private static partial void LogTileSearchOutOfTime(ILogger logger, int tile, int next);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Chose tile {Tile} (smallest rejected {Rejected}, 0 = none)")]
    private static partial void LogTileChosen(ILogger logger, int tile, int rejected);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "{Step}: {Count} of {Planned} timed runs fit the time budget")]
    private static partial void LogTimedRunsCut(ILogger logger, string step, int count, int planned);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Reference benchmark at tile {Tile} ({Precision}): {Ms:F1} ms")]
    private static partial void LogReference(ILogger logger, int tile, string precision, double ms);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Tile {Tile}: first run {Slowdown:F2}× slower per pixel than the reference (limit {Limit:F2}×); treating it as too big")]
    private static partial void LogProbeTooSlow(ILogger logger, int tile, double slowdown, double limit);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Calibrated {Family} on {Adapter}: {Precision}, tile {Tile}, {SecondsPerMegapixel:F2} s/MP in {Seconds:F1} s")]
    private static partial void LogCalibrated(ILogger logger, string family, string adapter, string precision, int tile, double secondsPerMegapixel, double seconds);

    private sealed record CalibrationRun(
        GpuAdapterInfo Adapter,
        CalibrationOptions Options,
        OnnxRunnerOptions RunnerOptions,
        IProgress<CalibrationProgress>? Progress,
        long StartTimestamp,
        ulong? BaselineUsage,
        ulong AllowanceBytes,
        CancellationToken CancellationToken)
    {
        public TimeSpan Elapsed(TimeProvider timeProvider) => timeProvider.GetElapsedTime(StartTimestamp);

        public void Report(string step, int done, int total) => Progress?.Report(new CalibrationProgress(step, done, total));
    }
}
