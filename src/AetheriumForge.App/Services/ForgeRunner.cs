using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Services;

/// <summary>
/// Runs the pipeline for the open project (IMPLEMENTATION_GUIDE §11, §14 "Run"). A singleton, so a run survives page navigation.
/// </summary>
/// <remarks>
/// <para>Each run picks the adapter (<see cref="GpuProfiler.SelectAdapter(IReadOnlyList{GpuAdapterInfo})"/>), looks up the GPU profile of every
/// model family the queued jobs use, re-resolves the profile's DirectML device from the current adapters, and uses the profile's precision
/// variant, tile size and throughput (as the ETA seed). Without a profile a model runs on the selected adapter with a 384 px tile and an
/// unseeded ETA. Calibration belongs to the Models page.</para>
/// <para>The per-run services (GPU sessions, archive readers) are rebuilt for every run and disposed when it ends. texconv processes are tracked
/// and killed when the app exits.</para>
/// </remarks>
public sealed partial class ForgeRunner : IForgeRunner, IDisposable
{
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProjectSession _session;
    private readonly IGpuAdapterProvider _adapters;
    private readonly IForgeRunBackend _backend;
    private readonly ChildProcessTracker _processes;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ActiveRun? _active;
    private PipelineProgress? _lastProgress;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForgeRunner"/> class.
    /// </summary>
    /// <param name="session">The open project.</param>
    /// <param name="adapters">Enumerates GPU adapters.</param>
    /// <param name="profiles">The GPU profile store.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public ForgeRunner(IProjectSession session, IGpuAdapterProvider adapters, GpuProfileStore profiles, ILoggerFactory loggerFactory)
        : this(session, adapters, new ChildProcessTracker(), profiles, loggerFactory)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ForgeRunner"/> class with a custom backend (tests).</summary>
    /// <param name="session">The open project.</param>
    /// <param name="adapters">Enumerates GPU adapters.</param>
    /// <param name="backend">The backend.</param>
    /// <param name="processes">The child-process tracker.</param>
    /// <param name="logger">Logger.</param>
    internal ForgeRunner(IProjectSession session, IGpuAdapterProvider adapters, IForgeRunBackend backend, ChildProcessTracker processes, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _adapters = adapters;
        _backend = backend;
        _processes = processes;
        _logger = logger;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private ForgeRunner(IProjectSession session, IGpuAdapterProvider adapters, ChildProcessTracker processes, GpuProfileStore profiles, ILoggerFactory loggerFactory)
        : this(session, adapters, new ForgeRunBackend(profiles, processes, loggerFactory), processes, loggerFactory.CreateLogger<ForgeRunner>())
    {
    }

    /// <inheritdoc />
    public event EventHandler<PipelineProgress>? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    /// <inheritdoc />
    public PipelineState State
    {
        get
        {
            lock (_gate)
            {
                if (_active is not { } run)
                {
                    return PipelineState.Idle;
                }

                if (run.StopRequested)
                {
                    return PipelineState.Stopping;
                }

                return run.PauseRequested ? PipelineState.Paused : PipelineState.Running;
            }
        }
    }

    /// <inheritdoc />
    public PipelineProgress? LastProgress => Volatile.Read(ref _lastProgress);

    /// <summary>Gets the tracker of texconv processes; killed when the app exits.</summary>
    internal ChildProcessTracker Processes => _processes;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A run is already active, no project is open, or a model is not installed.</exception>
    /// <exception cref="FileNotFoundException">texconv was not found.</exception>
    public async Task<PipelineRunSummary> StartAsync(ForgeRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = new ActiveRun();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
            {
                run.Dispose();
                throw new InvalidOperationException("A run is already in progress.");
            }

            _active = run;
        }

        PipelineRunSummary? summary = null;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            RunStateChanged?.Invoke(this, null);
            var plan = await BuildPlanAsync(request, run.Stop.Token).ConfigureAwait(false);
            using var resources = _backend.CreateRun(plan);
            using var throttled = new ThrottledProgress<PipelineProgress>(p => OnProgress(run, p), _progressInterval);
            var pipeline = resources.Pipeline;
            LogRunStarting(_logger, plan.Project.Root, plan.Options.MaxJobs, plan.Options.ModelOverride, plan.Models.Count);

            Task<PipelineRunSummary> running;
            lock (_gate)
            {
                run.Pipeline = pipeline;
                running = pipeline.RunAsync(throttled, run.Stop.Token);
                if (run.PauseRequested)
                {
                    pipeline.Pause();
                }
            }

            try
            {
                summary = await running.ConfigureAwait(false);
            }
            finally
            {
                throttled.Flush();
            }

            LogRunEnded(_logger, summary.Done, summary.Failed, summary.Requeued, summary.Stopped, summary.Elapsed);
            return summary;
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
        {
            // Stopped while planning: nothing ran.
            summary = new PipelineRunSummary(0, 0, 0, true, TimeProvider.System.GetElapsedTime(started));
            return summary;
        }
        catch (Exception ex)
        {
            LogRunFailed(_logger, ex);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _active = null;
            }

            run.Dispose();
            try
            {
                NotifyJobsChanged();
            }
            finally
            {
                // Always signal the end: the close prompt and the Run page wait for it.
                RunStateChanged?.Invoke(this, summary ?? FallbackSummary(run, TimeProvider.System.GetElapsedTime(started)));
            }
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            if (_active is not { StopRequested: false } run || run.PauseRequested)
            {
                return;
            }

            run.PauseRequested = true;
            run.Pipeline?.Pause();
        }

        LogPaused(_logger);
    }

    /// <inheritdoc />
    public void Unpause()
    {
        lock (_gate)
        {
            if (_active is not { StopRequested: false, PauseRequested: true } run)
            {
                return;
            }

            run.PauseRequested = false;
            run.Pipeline?.Unpause();
        }

        LogResumed(_logger);
    }

    /// <inheritdoc />
    public void RequestStop()
    {
        ActiveRun? run;
        lock (_gate)
        {
            run = _active;
            if (run is null || run.StopRequested)
            {
                return;
            }

            run.StopRequested = true;

            // Cancelled under the lock: the run disposes its token source only after it left _active.
            run.Stop.Cancel();
        }

        LogStopRequested(_logger);
    }

    /// <summary>Kills tracked texconv processes. A run still in progress is left to the process exit.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        KillChildProcesses();
    }

    /// <summary>Resolves the plan of a run: project, queued jobs, models, GPU adapter and profiles, texconv.</summary>
    /// <param name="request">The run options.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan.</returns>
    internal async Task<ForgeRunPlan> BuildPlanAsync(ForgeRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = _session.Current ?? throw new InvalidOperationException("No project is open. Open or create one on the Scan page first.");
        var store = _session.Store ?? throw new InvalidOperationException("The project's job store is not open.");
        var modelOverride = string.IsNullOrWhiteSpace(request.ModelOverride) ? null : request.ModelOverride.Trim();
        if (request.MaxJobs is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxJobs, "The job limit must be 1 or greater.");
        }

        var texconv = _backend.FindTexconv();
        var installed = _backend.DiscoverModels();

        // Interrupted (Running) jobs are requeued by the pipeline at start, so their models are planned too.
        var jobs = await PeekJobsAsync(store, request.MaxJobs, cancellationToken).ConfigureAwait(false);
        var required = ForgeRunPlanning.RequiredModels(jobs, modelOverride);

        var adapters = _adapters.GetAdapters();
        var selected = new GpuProfiler(_adapters).SelectAdapter(adapters);
        if (selected is null)
        {
            LogNoAdapter(_logger);
        }

        var defaultDevice = selected?.DirectMLDeviceId ?? 0;
        var models = new Dictionary<string, ModelRunPlan>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var model in required)
        {
            var family = GpuProfiler.GetModelFamily(model);
            var profile = selected is null ? null : await _backend.FindProfileAsync(selected, family, cancellationToken).ConfigureAwait(false);
            var variant = ForgeRunPlanning.ChooseVariant(model, profile, installed);
            if (variant is null)
            {
                missing.Add(model);
                continue;
            }

            var (name, precision) = variant.Value;
            ModelRunPlan plan;
            if (profile is null)
            {
                plan = new ModelRunPlan(model, name, defaultDevice, ForgeRunPlanning.DefaultTileSize, null, null);
                LogNoProfile(_logger, model, name, precision, defaultDevice, ForgeRunPlanning.DefaultTileSize);
            }
            else
            {
                // The profile's device index is a snapshot; resolve it again from the current adapters.
                var device = GpuProfiler.ResolveAdapter(profile, adapters)?.DirectMLDeviceId ?? defaultDevice;
                if (string.Equals(precision, profile.Precision, StringComparison.OrdinalIgnoreCase))
                {
                    plan = new ModelRunPlan(model, name, device, profile.TileSize, profile.SecondsPerMegapixel, profile);
                    LogProfileUsed(_logger, model, name, precision, device, profile.TileSize, profile.SecondsPerMegapixel);
                }
                else
                {
                    // The profile measured another precision: its tile and throughput do not apply to this variant.
                    plan = new ModelRunPlan(model, name, device, ForgeRunPlanning.DefaultTileSize, null, profile);
                    LogProfilePrecisionMismatch(_logger, model, name, precision, profile.Precision, device, ForgeRunPlanning.DefaultTileSize);
                }
            }

            models[model] = plan;
        }

        if (missing.Count > 0)
        {
            var have = installed.Count == 0 ? "none" : string.Join(", ", installed.Select(m => m.Name));
            throw new InvalidOperationException(
                $"Model(s) not installed in {_backend.ModelsFolder}: {string.Join(", ", missing)} (installed: {have}). "
                + "Install them on the Models page, or pick an installed model as the override.");
        }

        var options = new PipelineOptions
        {
            WorkRoot = project.WorkRoot,
            OutputRoot = project.OutputRoot,
            MaxJobs = request.MaxJobs,
            ModelOverride = modelOverride,
        };
        return new ForgeRunPlan(project, store, options, texconv, selected?.DirectMLDeviceId, installed, models, defaultDevice);
    }

    private static async Task<List<JobRecord>> PeekJobsAsync(JobStore store, int? limit, CancellationToken cancellationToken)
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in store.GetJobsAsync(JobState.Running, cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(job);
        }

        var queued = 0;
        await foreach (var job in store.GetJobsAsync(JobState.Queued, cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(job);
            if (limit is { } max && ++queued >= max)
            {
                break;
            }
        }

        return jobs;
    }

    // The run failed: report what this run had finished according to its last snapshot.
    private static PipelineRunSummary FallbackSummary(ActiveRun run, TimeSpan elapsed)
    {
        var last = run.LastProgress;
        return new PipelineRunSummary(last?.Done ?? 0, last?.Failed ?? 0, 0, run.StopRequested, last?.Elapsed ?? elapsed);
    }

    private void OnProgress(ActiveRun run, PipelineProgress progress)
    {
        run.LastProgress = progress;
        Volatile.Write(ref _lastProgress, progress);
        ProgressChanged?.Invoke(this, progress);
    }

    private void OnProcessExit(object? sender, EventArgs e) => KillChildProcesses();

    private void NotifyJobsChanged()
    {
        try
        {
            _session.NotifyJobsChanged();
        }
        catch (Exception ex)
        {
            // A failing JobsChanged subscriber must not keep RunStateChanged from firing; it is logged, not rethrown.
            LogNotifyJobsChangedFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Kills every tracked texconv process (and its children). Used on exit and by the close prompt's "Force close"; jobs left Running
    /// are requeued by crash recovery on the next start.
    /// </summary>
    /// <returns>The number of processes a kill was sent to.</returns>
    public int KillChildProcesses()
    {
        var killed = _processes.KillAll();
        if (killed > 0)
        {
            LogKilledChildren(_logger, killed);
        }

        return killed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run starting for {Project}: limit {MaxJobs}, model override {ModelOverride}, {ModelCount} model(s)")]
    private static partial void LogRunStarting(ILogger logger, string project, int? maxJobs, string? modelOverride, int modelCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run ended: {Done} done, {Failed} failed, {Requeued} requeued, stopped {Stopped}, elapsed {Elapsed}")]
    private static partial void LogRunEnded(ILogger logger, int done, int failed, int requeued, bool stopped, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Error, Message = "The run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pause requested")]
    private static partial void LogPaused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resume requested")]
    private static partial void LogResumed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stop requested")]
    private static partial void LogStopRequested(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No usable GPU adapter found; using DirectML device 0")]
    private static partial void LogNoAdapter(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model}: GPU profile used, running {Variant} ({Precision}) on device {Device}, tile {Tile} px, ETA seed {SecondsPerMegapixel:F3} s/MP")]
    private static partial void LogProfileUsed(ILogger logger, string model, string variant, string precision, int device, int tile, double secondsPerMegapixel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model}: the GPU profile is {ProfilePrecision} but {Variant} ({Precision}) runs; device {Device}, default tile {Tile} px, no ETA seed")]
    private static partial void LogProfilePrecisionMismatch(ILogger logger, string model, string variant, string precision, string profilePrecision, int device, int tile);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model}: no GPU profile, running {Variant} ({Precision}) on device {Device}, tile {Tile} px, no ETA seed (calibrate on the Models page)")]
    private static partial void LogNoProfile(ILogger logger, string model, string variant, string precision, int device, int tile);

    [LoggerMessage(Level = LogLevel.Error, Message = "A JobsChanged subscriber failed after the run")]
    private static partial void LogNotifyJobsChangedFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killed {Count} texconv process(es) on exit")]
    private static partial void LogKilledChildren(ILogger logger, int count);

    private sealed class ActiveRun : IDisposable
    {
        public CancellationTokenSource Stop { get; } = new();

        public IForgePipeline? Pipeline { get; set; }

        public bool PauseRequested { get; set; }

        public bool StopRequested { get; set; }

        public PipelineProgress? LastProgress { get; set; }

        public void Dispose() => Stop.Dispose();
    }
}
