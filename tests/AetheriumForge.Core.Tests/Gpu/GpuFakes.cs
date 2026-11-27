using System.Reflection;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace AetheriumForge.Core.Tests.Gpu;

/// <summary>A manual clock: time only moves when a fake runner advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

/// <summary>A synchronous progress sink (System.Progress posts asynchronously).</summary>
internal sealed class ListProgress : IProgress<CalibrationProgress>
{
    public List<CalibrationProgress> Reports { get; } = [];

    public void Report(CalibrationProgress value)
    {
        lock (Reports)
        {
            Reports.Add(value);
        }
    }
}

/// <summary>
/// A simulated GPU: an adapter provider whose video-memory usage follows the allocations of the <see cref="SimulatedRunner"/>s it creates
/// (a scripted memory curve), plus the runner factory for <see cref="GpuProfiler"/>.
/// </summary>
internal sealed class SimulatedGpu : IGpuAdapterProvider
{
    public const long Luid = 0x1234;

    public static readonly GpuAdapterInfo Adapter = new(Luid, 0, "Simulated GPU 8GB", 0x10DE, 0x1B80, 8UL << 30, 16UL << 30, false, false, "32.0.15.6094");

    private readonly Lock _gate = new();
    private ulong _usage;
    private int _queryCount;
    private int _activeRuns;

    public ManualTimeProvider Time { get; } = new();

    /// <summary>Gets or sets the OS budget in bytes.</summary>
    public ulong Budget { get; set; } = 8UL << 30;

    /// <summary>Gets or sets the usage of other work in this process before calibration.</summary>
    public ulong BaseUsage { get; set; } = 1UL << 30;

    /// <summary>Gets or sets a value indicating whether <see cref="QueryVideoMemory"/> reports memory.</summary>
    public bool ReportsMemory { get; set; } = true;

    /// <summary>Gets or sets the bytes the model weights take once loaded.</summary>
    public ulong WeightBytes { get; set; } = 64UL << 20;

    /// <summary>Gets or sets the peak bytes one run needs for a square input of the given side (the memory curve).</summary>
    public Func<int, ulong> MemoryCurve { get; set; } = side => (ulong)side * (ulong)side * 8192;

    /// <summary>Gets or sets the run duration of a precision for a square input of the given side.</summary>
    public Func<string, int, TimeSpan> Duration { get; set; } = (_, side) => TimeSpan.FromTicks((long)side * side);

    /// <summary>Gets or sets the input side above which a run throws an out-of-memory error, or <see langword="null"/>.</summary>
    public int? OutOfMemoryAboveSide { get; set; }

    /// <summary>Gets or sets the error thrown when a run fails, by precision; <see langword="null"/> for none.</summary>
    public Func<string, int, Exception?> Failure { get; set; } = (_, _) => null;

    /// <summary>Gets or sets a hook called before each run with the 1-based run number.</summary>
    public Action<int>? BeforeRun { get; set; }

    /// <summary>Gets or sets the error thrown by the runner factory, by 1-based creation number; <see langword="null"/> for none.</summary>
    public Func<int, ModelInfo, Exception?> CreateFailure { get; set; } = (_, _) => null;

    /// <summary>Gets or sets a duration multiplier by 0-based runner index.</summary>
    public Func<int, double> DurationScale { get; set; } = _ => 1;

    /// <summary>
    /// Gets or sets bytes held only during a run (freed before it returns) by input side. A run that holds transient memory waits
    /// until the profiler samples memory, so the test proves the peak is sampled during the run.
    /// </summary>
    public Func<int, ulong> TransientBytes { get; set; } = _ => 0;

    public ulong CurrentUsage
    {
        get
        {
            lock (_gate)
            {
                return _usage;
            }
        }
    }

    public int QueryCount => Volatile.Read(ref _queryCount);

    public int MaxConcurrentRuns { get; private set; }

    public List<(string Model, OnnxRunnerOptions Options)> Created { get; } = [];

    public List<(string Precision, int Side)> Runs { get; } = [];

    public List<SimulatedRunner> Runners { get; } = [];

    public IReadOnlyList<GpuAdapterInfo> GetAdapters() => [Adapter];

    public VideoMemoryInfo? QueryVideoMemory(long luid)
    {
        Interlocked.Increment(ref _queryCount);
        lock (_gate)
        {
            return ReportsMemory && luid == Luid ? new VideoMemoryInfo(Budget, BaseUsage + _usage, Budget, 0) : null;
        }
    }

    public IModelRunner Create(ModelInfo model, OnnxRunnerOptions options)
    {
        lock (_gate)
        {
            Created.Add((model.Name, options));
            if (CreateFailure(Created.Count, model) is { } failure)
            {
                throw failure;
            }

            var runner = new SimulatedRunner(this, model.Precision, Runners.Count);
            Runners.Add(runner);
            Allocate(WeightBytes);
            return runner;
        }
    }

    public GpuProfiler CreateProfiler() => new(this, Create, Time);

    internal void Allocate(ulong bytes)
    {
        lock (_gate)
        {
            _usage += bytes;
        }
    }

    internal void Free(ulong bytes)
    {
        lock (_gate)
        {
            _usage -= bytes;
        }
    }

    internal int RecordRun(string precision, int side)
    {
        lock (_gate)
        {
            Runs.Add((precision, side));
            _activeRuns++;
            MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, _activeRuns);
            return Runs.Count;
        }
    }

    internal void EndRun()
    {
        lock (_gate)
        {
            _activeRuns--;
        }
    }

    public static ModelInfo Model(string name, string precision) => new(name, $"C:\\models\\{name}.onnx", name, 4, precision, "BSD-3-Clause", ["diffuse"]);

    /// <summary>Builds the exception ONNX Runtime's DirectML provider throws when it runs out of memory.</summary>
    public static OnnxRuntimeException OutOfMemoryError() => CreateOnnxRuntimeException(
        "[ErrorCode:RuntimeException] Non-zero status code returned while running Conv node. Status Message: DmlCommittedResourceAllocator.cpp(22)\\onnxruntime.dll!00007FF8: (caller: 00007FF8) Exception(3) tid(1f24) 8007000E Not enough memory resources are available to complete this operation.");

    public static OnnxRuntimeException CreateOnnxRuntimeException(string message)
    {
        // The constructor and its ErrorCode enum are internal to ONNX Runtime; build the exception the runtime would throw.
        var constructor = typeof(OnnxRuntimeException).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters() is [{ ParameterType.IsEnum: true }, { ParameterType: var text }] && text == typeof(string));
        var errorCode = Enum.Parse(constructor.GetParameters()[0].ParameterType, "RuntimeException");
        return (OnnxRuntimeException)constructor.Invoke([errorCode, message]);
    }
}

/// <summary>
/// A fake runner on a <see cref="SimulatedGpu"/>: like the DirectML allocator it keeps its largest allocation until disposed,
/// advances the simulated clock by the scripted duration, and throws an out-of-memory error above a scripted input size.
/// </summary>
internal sealed class SimulatedRunner(SimulatedGpu gpu, string precision, int index) : IModelRunner
{
    private ulong _allocated;

    public int Scale => 4;

    public bool Disposed { get; private set; }

    public PlanarImage Run(PlanarImage tile)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var run = gpu.RecordRun(precision, tile.Width);
        try
        {
            gpu.BeforeRun?.Invoke(run);
            if (gpu.Failure(precision, tile.Width) is { } failure)
            {
                throw failure;
            }

            if (tile.Width > gpu.OutOfMemoryAboveSide)
            {
                throw SimulatedGpu.OutOfMemoryError();
            }

            var needed = gpu.MemoryCurve(tile.Width);
            if (needed > _allocated)
            {
                gpu.Allocate(needed - _allocated);
                _allocated = needed;
            }

            if (gpu.TransientBytes(tile.Width) is var transient and > 0)
            {
                gpu.Allocate(transient);
                var queries = gpu.QueryCount;
                SpinWait.SpinUntil(() => gpu.QueryCount > queries, TimeSpan.FromSeconds(5));
                gpu.Free(transient);
            }

            gpu.Time.Advance(gpu.Duration(precision, tile.Width) * gpu.DurationScale(index));
            return new PlanarImage(tile.Channels, 1, 1);
        }
        finally
        {
            gpu.EndRun();
        }
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        gpu.Free(_allocated + gpu.WeightBytes);
    }
}
