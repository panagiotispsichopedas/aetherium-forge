using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
/// <para>Calibration runs synchronously on a thread-pool thread and never runs two models at once. Calls for the same adapter are
/// serialised by this instance; calibration must still not share the adapter with a running pipeline (one GPU worker per adapter, §11).</para>
/// <para>The precision is chosen once, at the reference tile (<see cref="CalibrationOptions.PrecisionTileSize"/>), and then used at the
/// final tile. The FP16/FP32 ratio barely depends on the tile size, and benchmarking both precisions at every probed size would double
/// the calibration time.</para>
/// <para>Memory: every probed tile gets a fresh runner, which is warmed up once at the smallest tile before the baseline is read. During
/// the probe run <see cref="IGpuAdapterProvider.QueryVideoMemory"/> is sampled every <see cref="CalibrationOptions.MemorySampleInterval"/>;
/// the tile fits when the peak usage above that baseline stays within <see cref="CalibrationOptions.BudgetFraction"/> of the baseline's
/// <see cref="VideoMemoryInfo.Available"/>. A runner is never reused for a second tile size: DirectML keeps the buffers of every input
/// size a session has run, so a reused runner would charge the previous probe's buffers to the next tile and make its first run pay for
/// allocating next to them. Each probe therefore measures what a pipeline session that runs only that tile needs.</para>
/// <para>Speed: a probe whose first run is more than <see cref="CalibrationOptions.MaxProbeSlowdown"/> times slower per pixel than the
/// reference runs once more, and is rejected only when that warm run is too slow as well. A first run at a new size includes shader
/// compilation and buffer allocation; paging to shared memory slows every run.</para>
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
    private static readonly string[] _deviceRemovedMarkers = ["887A0005", "887A0006", "DXGI_ERROR_DEVICE_REMOVED", "DXGI_ERROR_DEVICE_HUNG"];

    private readonly IGpuAdapterProvider _adapterProvider;
    private readonly Func<ModelInfo, OnnxRunnerOptions, IModelRunner> _runnerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _adapterGates = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuProfiler"/> class.
    /// </summary>
    /// <param name="adapterProvider">Enumerates adapters and reads their video memory. <see cref="IGpuAdapterProvider.QueryVideoMemory"/> is also called from a timer thread.</param>
    /// <param name="runnerFactory">Creates a model runner; defaults to <see cref="OnnxModelRunner.Create"/>.</param>
    /// <param name="timeProvider">Clock for benchmarks, memory sampling and the calibration time stamp; defaults to <see cref="TimeProvider.System"/>.</param>
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
    /// Finds the current adapter a stored profile was calibrated on. <see cref="GpuProfile.AdapterLuid"/> and
    /// <see cref="GpuProfile.DirectMLDeviceId"/> are snapshots, so resolve the adapter from a fresh
    /// <see cref="IGpuAdapterProvider.GetAdapters"/> list before creating a session.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="adapters">The current adapters.</param>
    /// <returns>
    /// The hardware adapter with the profile's vendor id, device id and name; the one with the same LUID first, then the lowest DirectML
    /// device id. <see langword="null"/> when none matches.
    /// </returns>
    public static GpuAdapterInfo? ResolveAdapter(GpuProfile profile, IReadOnlyList<GpuAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(adapters);
        return adapters
            .Where(adapter => !adapter.IsSoftware && profile.MatchesHardware(adapter))
            .OrderBy(adapter => adapter.Luid == profile.AdapterLuid ? 0 : 1)
            .ThenBy(adapter => adapter.DirectMLDeviceId)
            .FirstOrDefault();
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
    /// Calibrates one model family on one adapter: precision, tile size and throughput. Calls for the same adapter wait for each other.
    /// </summary>
    /// <param name="adapter">The adapter to calibrate.</param>
    /// <param name="modelFamilyVariants">The FP32 and optional FP16 variant of one model family (by <see cref="ModelInfo.Precision"/>).</param>
    /// <param name="options">Calibration options; defaults when <see langword="null"/>.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the calibration between model runs.</param>
    /// <returns>
    /// The profile. When the FP16 variant failed to load and FP32 was kept, <see cref="GpuProfile.IsProvisional"/> is
    /// <see langword="true"/> and <see cref="GpuProfileStore"/> does not persist it.
    /// </returns>
    /// <exception cref="ArgumentException">The variants are empty, from different families, or have no FP32/FP16 precision.</exception>
    /// <exception cref="VideoMemoryExhaustedException">Even the smallest tile does not fit in video memory, or runs too slowly.</exception>
    /// <exception cref="OnnxRuntimeException">A model run failed for another reason, for example device removal.</exception>
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
        var gate = _adapterGates.GetOrAdd(adapter.Luid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Calibrate(adapter, family, fp32, fp16, options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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

    /// <summary>
    /// Returns <see langword="true"/> when an error reports that the GPU was removed or reset (<c>DXGI_ERROR_DEVICE_REMOVED</c> /
    /// <c>DXGI_ERROR_DEVICE_HUNG</c>); the device is unusable and no fallback applies.
    /// </summary>
    /// <param name="exception">The exception to inspect, including inner exceptions.</param>
    /// <returns><see langword="true"/> for device removal.</returns>
    internal static bool IsDeviceRemoved(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (_deviceRemovedMarkers.Any(marker => current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
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

    private static bool IsLoadFailure(Exception exception) =>
        exception is OnnxRuntimeException or InvalidDataException or FileNotFoundException
        && !VideoMemoryErrors.IsOutOfMemory(exception)
        && !IsDeviceRemoved(exception);

    private GpuProfile Calibrate(
        GpuAdapterInfo adapter,
        string family,
        ModelInfo? fp32,
        ModelInfo? fp16,
        CalibrationOptions options,
        IProgress<CalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var initial = _adapterProvider.QueryVideoMemory(adapter.Luid);
        var available = initial?.Available ?? FallbackMemory(adapter);
        var run = new CalibrationRun(
            adapter,
            options,
            new OnnxRunnerOptions { DeviceId = adapter.DirectMLDeviceId, UseDirectML = options.UseDirectML },
            progress,
            _timeProvider.GetTimestamp(),
            cancellationToken);
        var estimate = EstimateTileSize((ulong)(available * options.BudgetFraction), options);
        LogCalibrationStarted(_logger, family, adapter.Name, available / (1024 * 1024), estimate, initial is not null);

        var reference = ChoosePrecision(run, fp32, fp16, estimate);
        var precision = string.Equals(reference.Model.Precision, Fp16, StringComparison.OrdinalIgnoreCase) ? Fp16 : Fp32;

        var (runner, tileSize, warmRuns) = FindTileSize(run, reference, estimate);
        double secondsPerTile;
        using (runner)
        {
            var warmups = Math.Max(0, options.WarmupRuns - warmRuns);
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
            IsProvisional = reference.Provisional,
        };

        var totalSeconds = run.Elapsed(_timeProvider).TotalSeconds;
        LogCalibrated(_logger, family, adapter.Name, precision, tileSize, secondsPerMegapixel, totalSeconds);
        return profile;
    }

    private Reference ChoosePrecision(CalibrationRun run, ModelInfo? fp32, ModelInfo? fp16, int estimate)
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
                    var seconds = BenchmarkModel(run, CreateRunner(run, only), input);
                    LogReference(_logger, tile, only.Precision, seconds * 1000);
                    return new Reference(only, tile, seconds / pixels, Provisional: false);
                }

                var fp32Seconds = BenchmarkModel(run, CreateRunner(run, fp32), input);
                IModelRunner fp16Runner;
                try
                {
                    fp16Runner = CreateRunner(run, fp16);
                }
                catch (Exception exception) when (IsLoadFailure(exception))
                {
                    // Only a model that cannot be loaded falls back; run-time errors propagate. The profile is provisional and not persisted.
                    LogFp16Unusable(_logger, exception, fp16.Name);
                    return new Reference(fp32, tile, fp32Seconds / pixels, Provisional: true);
                }

                var fp16Seconds = BenchmarkModel(run, fp16Runner, input);
                var useFp16 = fp16Seconds * (1 + options.Fp16MinimumSpeedup) < fp32Seconds;
                LogPrecisionChosen(_logger, tile, fp32Seconds * 1000, fp16Seconds * 1000, useFp16 ? Fp16 : Fp32);
                return useFp16
                    ? new Reference(fp16, tile, fp16Seconds / pixels, Provisional: false)
                    : new Reference(fp32, tile, fp32Seconds / pixels, Provisional: false);
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

    private IModelRunner CreateRunner(CalibrationRun run, ModelInfo model)
    {
        run.CancellationToken.ThrowIfCancellationRequested();
        return _runnerFactory(model, run.RunnerOptions);
    }

    private double BenchmarkModel(CalibrationRun run, IModelRunner runner, PlanarImage input)
    {
        using (runner)
        {
            return Benchmark(run, runner, input, run.Options.WarmupRuns, PrecisionStep);
        }
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

    private (IModelRunner Runner, int TileSize, int WarmRuns) FindTileSize(CalibrationRun run, Reference reference, int estimate)
    {
        var options = run.Options;
        int? largestFit = null;
        int? smallestFailure = null;
        var lastFailure = ProbeOutcome.Fits;
        var fitDuration = TimeSpan.Zero;
        var setupDuration = TimeSpan.Zero;
        var warmRuns = 0;
        var tile = estimate;
        ProbeRunner? probeRunner = null;
        try
        {
            for (var probe = 1; ; probe++)
            {
                run.CancellationToken.ThrowIfCancellationRequested();

                // Every tile gets a fresh runner (see the class remarks): the previous probe's buffers must not count against this one.
                probeRunner?.Runner.Dispose();
                probeRunner = null;
                var setupStarted = _timeProvider.GetTimestamp();
                probeRunner = CreateProbeRunner(run, reference.Model, tile);
                setupDuration = _timeProvider.GetElapsedTime(setupStarted);
                var result = probeRunner is null ? new ProbeResult(ProbeOutcome.OutOfMemory, TimeSpan.Zero, 0) : Probe(run, probeRunner, tile, reference);
                if (result.Outcome == ProbeOutcome.Fits)
                {
                    largestFit = tile;
                    fitDuration = result.Duration;
                    warmRuns = result.Runs;
                }
                else
                {
                    smallestFailure = tile;
                    lastFailure = result.Outcome;
                    probeRunner?.Runner.Dispose();
                    probeRunner = null;
                    warmRuns = 0;
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

                    // Growing costs a new runner (load and warm-up), the probe, and the throughput runs (warm-ups after the probe and at
                    // least one timed run) at the next size, each about duration × (input area ratio).
                    var ratio = Math.Pow((next + (2.0 * options.Overlap)) / (fit + (2.0 * options.Overlap)), 2);
                    var predicted = setupDuration + (fitDuration * ratio * (Math.Max(1, options.WarmupRuns) + 1));
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
                var reason = lastFailure == ProbeOutcome.TooSlow
                    ? $"runs more than {options.MaxProbeSlowdown:0.##}× slower per pixel than the reference benchmark (video memory is probably paged to shared memory)"
                    : "does not fit in video memory";
                throw new VideoMemoryExhaustedException($"Even a {options.MinTileSize}-pixel tile {reason} on {run.Adapter.Name}.");
            }

            // Keep the runner only when it belongs to the chosen tile; a runner that ran another size would carry that size's buffers.
            var runner = tile == chosen ? probeRunner?.Runner : null;
            if (runner is null)
            {
                probeRunner?.Runner.Dispose();
                probeRunner = null;
                runner = CreateRunner(run, reference.Model);
                warmRuns = 0;
            }

            LogTileChosen(_logger, chosen, smallestFailure ?? 0);
            return (runner, chosen, warmRuns);
        }
        catch
        {
            probeRunner?.Runner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a probe runner, warms it up once at the smallest tile and reads the memory baseline.
    /// Returns <see langword="null"/> when the device runs out of memory while loading or warming up.
    /// </summary>
    private ProbeRunner? CreateProbeRunner(CalibrationRun run, ModelInfo model, int tile)
    {
        IModelRunner? runner = null;
        try
        {
            runner = CreateRunner(run, model);
            run.CancellationToken.ThrowIfCancellationRequested();
            runner.Run(CreateInput(run.Options.MinTileSize + (2 * run.Options.Overlap)));
            var baseline = _adapterProvider.QueryVideoMemory(run.Adapter.Luid);
            return new ProbeRunner(runner, baseline);
        }
        catch (Exception exception) when (VideoMemoryErrors.IsOutOfMemory(exception))
        {
            runner?.Dispose();
            LogProbeOutOfMemory(_logger, tile, exception.Message);
            LogProbeTrace(_logger, tile, null, null, null, null, 0, ProbeOutcome.OutOfMemory, "out of memory while loading or warming up the probe runner");
            return null;
        }
        catch
        {
            runner?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns how many times slower per input pixel a run at <paramref name="tile"/> is than the reference benchmark, or
    /// <see langword="null"/> when the slowdown rule does not apply (below the reference tile, or no usable reference).
    /// </summary>
    private static double? Slowdown(CalibrationRun run, int tile, TimeSpan duration, Reference reference)
    {
        // Without a memory reading (or when the driver pages video memory out to shared memory instead of failing), a tile that is too
        // big shows up as runs that are several times slower per pixel than the reference benchmark. Smaller tiles than the reference are
        // naturally less efficient, so the rule only applies at or above the reference size.
        if (tile < reference.TileSize || reference.SecondsPerInputPixel <= 0)
        {
            return null;
        }

        var side = tile + (2.0 * run.Options.Overlap);
        var slowdown = duration.TotalSeconds / (side * side) / reference.SecondsPerInputPixel;
        return double.IsNaN(slowdown) ? null : slowdown;
    }

    private static ulong? ToMiB(ulong? bytes) => bytes / (1024 * 1024);

    private ProbeResult Probe(CalibrationRun run, ProbeRunner probeRunner, int tile, Reference reference)
    {
        var options = run.Options;
        var input = CreateInput(tile + (2 * options.Overlap));
        var baselineMiB = ToMiB(probeRunner.Baseline?.CurrentUsage);
        ulong? peak;
        TimeSpan duration;
        using (var sampler = new MemorySampler(_adapterProvider, run.Adapter.Luid, _timeProvider, options.MemorySampleInterval))
        {
            var started = _timeProvider.GetTimestamp();
            try
            {
                probeRunner.Runner.Run(input);
            }
            catch (Exception exception) when (VideoMemoryErrors.IsOutOfMemory(exception))
            {
                var failed = _timeProvider.GetElapsedTime(started);
                LogProbeOutOfMemory(_logger, tile, exception.Message);
                LogProbeTrace(_logger, tile, baselineMiB, null, null, null, failed.TotalMilliseconds, ProbeOutcome.OutOfMemory, "out of memory during the probe run");
                return new ProbeResult(ProbeOutcome.OutOfMemory, failed, 1);
            }

            duration = _timeProvider.GetElapsedTime(started);
            peak = sampler.Stop();
        }

        ulong? used = null;
        ulong? allowance = null;
        if (peak is { } peakUsage && probeRunner.Baseline is { } baseline)
        {
            used = peakUsage > baseline.CurrentUsage ? peakUsage - baseline.CurrentUsage : 0;
            allowance = (ulong)(baseline.Available * options.BudgetFraction);
        }

        var peakMiB = ToMiB(peak);
        var usedMiB = ToMiB(used);
        var allowanceMiB = ToMiB(allowance);
        if (used > allowance)
        {
            LogProbeTrace(_logger, tile, baselineMiB, peakMiB, usedMiB, allowanceMiB, duration.TotalMilliseconds, ProbeOutcome.OverBudget, "peak above baseline exceeds the allowance");
            return new ProbeResult(ProbeOutcome.OverBudget, duration, 1);
        }

        var memory = used is null ? "video memory not reported" : "peak within the allowance";
        if (Slowdown(run, tile, duration, reference) is not { } first || first <= options.MaxProbeSlowdown)
        {
            LogProbeTrace(_logger, tile, baselineMiB, peakMiB, usedMiB, allowanceMiB, duration.TotalMilliseconds, ProbeOutcome.Fits, memory);
            return new ProbeResult(ProbeOutcome.Fits, duration, 1);
        }

        // The first run at a new size also compiles shaders and allocates buffers; only a warm run that is still too slow means paging.
        run.CancellationToken.ThrowIfCancellationRequested();
        var warmStarted = _timeProvider.GetTimestamp();
        try
        {
            probeRunner.Runner.Run(input);
        }
        catch (Exception exception) when (VideoMemoryErrors.IsOutOfMemory(exception))
        {
            var failed = _timeProvider.GetElapsedTime(warmStarted);
            LogProbeOutOfMemory(_logger, tile, exception.Message);
            LogProbeTrace(_logger, tile, baselineMiB, peakMiB, usedMiB, allowanceMiB, failed.TotalMilliseconds, ProbeOutcome.OutOfMemory, "out of memory during the warm run");
            return new ProbeResult(ProbeOutcome.OutOfMemory, failed, 2);
        }

        var warmDuration = _timeProvider.GetElapsedTime(warmStarted);
        var warm = Slowdown(run, tile, warmDuration, reference) ?? 0;
        var detail = $"{memory}; first run {first:0.00}× and warm run {warm:0.00}× slower per pixel than the reference (limit {options.MaxProbeSlowdown:0.00}×)";
        if (warm > options.MaxProbeSlowdown)
        {
            LogProbeTooSlow(_logger, tile, warm, options.MaxProbeSlowdown);
            LogProbeTrace(_logger, tile, baselineMiB, peakMiB, usedMiB, allowanceMiB, warmDuration.TotalMilliseconds, ProbeOutcome.TooSlow, detail);
            return new ProbeResult(ProbeOutcome.TooSlow, warmDuration, 2);
        }

        LogProbeTrace(_logger, tile, baselineMiB, peakMiB, usedMiB, allowanceMiB, warmDuration.TotalMilliseconds, ProbeOutcome.Fits, detail);
        return new ProbeResult(ProbeOutcome.Fits, warmDuration, 2);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "No usable GPU adapter among {Count} reported adapters")]
    private static partial void LogNoAdapter(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Selected GPU adapter {Name} (device {DeviceId}, {DedicatedMiB} MiB dedicated, integrated {Integrated})")]
    private static partial void LogAdapterSelected(ILogger logger, string name, int deviceId, ulong dedicatedMiB, bool integrated);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "GPU adapter {Name} is tier {Tier}")]
    private static partial void LogTier(ILogger logger, string name, GpuTier tier);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Tier {Tier} recommends model family {Family}")]
    private static partial void LogRecommendation(ILogger logger, GpuTier tier, string family);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Calibrating {Family} on {Adapter}: {AvailableMiB} MiB available, first tile {Tile}, memory measured {Measured}")]
    private static partial void LogCalibrationStarted(ILogger logger, string family, string adapter, ulong availableMiB, int tile, bool measured);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Precision benchmark at tile {Tile}: fp32 {Fp32Ms:F1} ms, fp16 {Fp16Ms:F1} ms; chose {Precision}")]
    private static partial void LogPrecisionChosen(ILogger logger, int tile, double fp32Ms, double fp16Ms, string precision);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "FP16 model {Model} failed to load; keeping FP32 (the profile is provisional and will not be saved)")]
    private static partial void LogFp16Unusable(ILogger logger, Exception exception, string model);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Out of video memory in the precision benchmark at tile {Tile}; retrying at {Smaller}")]
    private static partial void LogPrecisionOutOfMemory(ILogger logger, int tile, int smaller);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Debug,
        Message = "Probe tile {Tile}: baseline {BaselineMiB} MiB, peak {PeakMiB} MiB, peak - baseline {UsedMiB} MiB, allowance {AllowanceMiB} MiB, {Ms:F0} ms; {Decision}: {Reason}")]
    private static partial void LogProbeTrace(ILogger logger, int tile, ulong? baselineMiB, ulong? peakMiB, ulong? usedMiB, ulong? allowanceMiB, double ms, ProbeOutcome decision, string reason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Tile {Tile}: out of video memory ({Reason})")]
    private static partial void LogProbeOutOfMemory(ILogger logger, int tile, string reason);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Tile search stops at {Tile}: trying {Next} would exceed the time budget")]
    private static partial void LogTileSearchOutOfTime(ILogger logger, int tile, int next);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Chose tile {Tile} (smallest rejected {Rejected}, 0 = none)")]
    private static partial void LogTileChosen(ILogger logger, int tile, int rejected);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "{Step}: {Count} of {Planned} timed runs fit the time budget")]
    private static partial void LogTimedRunsCut(ILogger logger, string step, int count, int planned);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Calibrated {Family} on {Adapter}: {Precision}, tile {Tile}, {SecondsPerMegapixel:F2} s/MP in {Seconds:F1} s")]
    private static partial void LogCalibrated(ILogger logger, string family, string adapter, string precision, int tile, double secondsPerMegapixel, double seconds);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Reference benchmark at tile {Tile} ({Precision}): {Ms:F1} ms")]
    private static partial void LogReference(ILogger logger, int tile, string precision, double ms);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Tile {Tile}: warm run {Slowdown:F2}× slower per pixel than the reference (limit {Limit:F2}×); treating it as too big")]
    private static partial void LogProbeTooSlow(ILogger logger, int tile, double slowdown, double limit);

    private enum ProbeOutcome
    {
        Fits,
        OutOfMemory,
        OverBudget,
        TooSlow,
    }

    private sealed record CalibrationRun(
        GpuAdapterInfo Adapter,
        CalibrationOptions Options,
        OnnxRunnerOptions RunnerOptions,
        IProgress<CalibrationProgress>? Progress,
        long StartTimestamp,
        CancellationToken CancellationToken)
    {
        public TimeSpan Elapsed(TimeProvider timeProvider) => timeProvider.GetElapsedTime(StartTimestamp);

        public void Report(string step, int done, int total) => Progress?.Report(new CalibrationProgress(step, done, total));
    }

    /// <summary>The chosen variant and its steady time per input pixel at the reference tile.</summary>
    private sealed record Reference(ModelInfo Model, int TileSize, double SecondsPerInputPixel, bool Provisional);

    /// <summary>A warmed-up probe runner and the memory reading taken after its warm-up.</summary>
    private sealed record ProbeRunner(IModelRunner Runner, VideoMemoryInfo? Baseline);

    /// <summary>A probe's decision, the run duration it is based on, and how many runs it made at the probed size.</summary>
    private readonly record struct ProbeResult(ProbeOutcome Outcome, TimeSpan Duration, int Runs);

    /// <summary>
    /// Samples the adapter's current usage on a timer while a probe runs and keeps the peak (IMPLEMENTATION_GUIDE §9, step 4).
    /// </summary>
    private sealed class MemorySampler : IDisposable
    {
        private readonly IGpuAdapterProvider _provider;
        private readonly long _luid;
        private readonly ITimer _timer;
        private readonly Lock _gate = new();
        private ulong? _peak;
        private bool _stopped;
        private ExceptionDispatchInfo? _failure;

        public MemorySampler(IGpuAdapterProvider provider, long luid, TimeProvider timeProvider, TimeSpan interval)
        {
            _provider = provider;
            _luid = luid;
            Sample();
            _timer = timeProvider.CreateTimer(_ => Sample(), null, interval, interval);
        }

        /// <summary>Stops sampling, takes a last sample and returns the peak usage, or <see langword="null"/> when memory is not reported.</summary>
        public ulong? Stop()
        {
            _timer.Dispose();
            Sample();
            lock (_gate)
            {
                _stopped = true;
                _failure?.Throw();
                return _peak;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopped = true;
            }

            _timer.Dispose();
        }

        private void Sample()
        {
            VideoMemoryInfo? info;
            try
            {
                info = _provider.QueryVideoMemory(_luid);
            }
            catch (Exception exception)
            {
                // A timer callback must not throw; the failure is rethrown by Stop on the calibration thread.
                lock (_gate)
                {
                    _failure ??= ExceptionDispatchInfo.Capture(exception);
                }

                return;
            }

            if (info is null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_stopped && (_peak is null || info.CurrentUsage > _peak))
                {
                    _peak = info.CurrentUsage;
                }
            }
        }
    }
}
