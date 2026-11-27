using System.Collections.ObjectModel;
using System.Globalization;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// A choice in the model-override list.
/// </summary>
/// <param name="Name">The model family, or <see langword="null"/> for each job's own model.</param>
/// <param name="Display">The text shown.</param>
public sealed record RunModelOption(string? Name, string Display)
{
    /// <summary>Gets the "each job's own model" choice.</summary>
    public static RunModelOption JobDefault { get; } = new(null, "Each job's own model");

    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>
/// The Run page (IMPLEMENTATION_GUIDE §14 "Run"): Start / Pause / Resume / Stop driven by the pipeline state machine
/// (Idle → Running ⇄ Paused → Stopping → Idle), run options, overall progress, per-stage lanes, the GPU memory bar, the live log tail and the
/// last run summary.
/// </summary>
/// <remarks>
/// Runner events arrive on any thread; they are coalesced and posted to the UI thread, so the UI is never blocked and never updated faster
/// than the runner's 100 ms throttle. The GPU memory bar is polled on a thread-pool thread at 1 Hz.
/// </remarks>
public sealed partial class RunViewModel : ObservableObject, IDisposable
{
    /// <summary>The GPU memory polling interval.</summary>
    public static readonly TimeSpan GpuPollInterval = TimeSpan.FromSeconds(1);

    private const double BytesPerGiB = 1024d * 1024 * 1024;

    private readonly IForgeRunner _runner;
    private readonly IProjectSession _session;
    private readonly IGpuAdapterProvider _adapters;
    private readonly ForgeRunnerLogSink _logSink;
    private readonly IRunUiDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private readonly Func<IReadOnlyList<ModelInfo>> _modelCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _gpuPollInterval;
    private readonly ILogger _logger;
    private readonly Lock _gpuGate = new();
    private CancellationTokenSource? _gpuPolling;
    private bool _disposed;
    private readonly Lock _logGate = new();
    private readonly List<string> _pendingLog = [];
    private PipelineProgress? _pendingProgress;
    private int _progressPosted;
    private int _logPosted;

    /// <summary>Initializes a new instance of the <see cref="RunViewModel"/> class.</summary>
    /// <param name="runner">The forge runner.</param>
    /// <param name="session">The open project.</param>
    /// <param name="adapters">GPU adapters, for the memory bar.</param>
    /// <param name="logSink">The log-tail sink.</param>
    /// <param name="navigation">Navigation: the GPU memory bar is polled only while the Run page is shown; also the "open Scan" link.</param>
    /// <param name="timeProvider">Clock for GPU polling.</param>
    /// <param name="logger">Logger.</param>
    public RunViewModel(
        IForgeRunner runner,
        IProjectSession session,
        IGpuAdapterProvider adapters,
        ForgeRunnerLogSink logSink,
        INavigationService navigation,
        TimeProvider timeProvider,
        ILogger<RunViewModel> logger)
        : this(runner, session, adapters, logSink, new AvaloniaRunUiDispatcher(), navigation, () => ModelCatalog.Discover(ModelCatalog.DefaultModelsFolder), timeProvider, GpuPollInterval, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RunViewModel"/> class (tests).</summary>
    /// <param name="runner">The forge runner.</param>
    /// <param name="session">The open project.</param>
    /// <param name="adapters">GPU adapters.</param>
    /// <param name="logSink">The log-tail sink.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="navigation">Navigation.</param>
    /// <param name="modelCatalog">Lists the installed models.</param>
    /// <param name="timeProvider">Clock for GPU polling.</param>
    /// <param name="gpuPollInterval">GPU polling interval, or <see langword="null"/> to disable polling.</param>
    /// <param name="logger">Logger.</param>
    internal RunViewModel(
        IForgeRunner runner,
        IProjectSession session,
        IGpuAdapterProvider adapters,
        ForgeRunnerLogSink logSink,
        IRunUiDispatcher dispatcher,
        INavigationService navigation,
        Func<IReadOnlyList<ModelInfo>> modelCatalog,
        TimeProvider timeProvider,
        TimeSpan? gpuPollInterval,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(logSink);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(modelCatalog);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _session = session;
        _adapters = adapters;
        _logSink = logSink;
        _dispatcher = dispatcher;
        _navigation = navigation;
        _modelCatalog = modelCatalog;
        _timeProvider = timeProvider;
        _gpuPollInterval = gpuPollInterval;
        _logger = logger;

        Lanes = [.. Enum.GetValues<PipelineStage>().Select(stage => new StageLaneViewModel(stage))];
        ModelOptions.Add(RunModelOption.JobDefault);
        _selectedModel = RunModelOption.JobDefault;
        _state = runner.State;
        _projectText = DescribeProject(session.Current);
        _hasProject = session.Current is not null;

        runner.ProgressChanged += OnRunnerProgress;
        runner.RunStateChanged += OnRunStateChanged;
        session.Changed += OnSessionChanged;
        logSink.LinesReceived += OnLogLines;
        navigation.Navigated += OnNavigated;

        if (runner.LastProgress is { } last)
        {
            ApplyProgress(last);
        }

        RefreshModels();
        UpdateGpuPolling(navigation.Current == AppPage.Run);
    }

    /// <summary>Gets a value indicating whether the GPU memory bar is being polled (only while the Run page is shown).</summary>
    internal bool IsGpuPolling
    {
        get
        {
            lock (_gpuGate)
            {
                return _gpuPolling is not null;
            }
        }
    }

    /// <summary>Gets the per-stage lanes, in pipeline order.</summary>
    public IReadOnlyList<StageLaneViewModel> Lanes { get; }

    /// <summary>Gets the model-override choices: each job's own model, then every installed model family.</summary>
    public ObservableCollection<RunModelOption> ModelOptions { get; } = [];

    /// <summary>Gets the live log tail, oldest first, at most <see cref="ForgeRunnerLogSink.Capacity"/> lines.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>Gets the pipeline state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(StopCommand))]
    private PipelineState _state;

    /// <summary>Gets a value indicating whether a start was requested and the run has not begun yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isStarting;

    /// <summary>Gets or sets the model override.</summary>
    [ObservableProperty]
    private RunModelOption? _selectedModel;

    /// <summary>Gets or sets a value indicating whether the run is limited to <see cref="JobLimit"/> jobs.</summary>
    [ObservableProperty]
    private bool _limitEnabled;

    /// <summary>Gets or sets the job limit.</summary>
    [ObservableProperty]
    private decimal? _jobLimit = 5;

    /// <summary>Gets the open project's description.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _projectText;

    /// <summary>Gets a value indicating whether a project is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProject))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _hasProject;

    /// <summary>Gets an error or hint, or <see langword="null"/>.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Gets jobs done in this run.</summary>
    [ObservableProperty]
    private int _done;

    /// <summary>Gets jobs failed in this run.</summary>
    [ObservableProperty]
    private int _failed;

    /// <summary>Gets jobs remaining.</summary>
    [ObservableProperty]
    private int _remaining;

    /// <summary>Gets done + failed + remaining.</summary>
    [ObservableProperty]
    private int _total;

    /// <summary>Gets the overall progress in percent.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Gets the counts line, for example <c>12 done · 1 failed · 87 remaining of 100</c>.</summary>
    [ObservableProperty]
    private string _countsText = "No run yet";

    /// <summary>Gets the ETA text (<see cref="EtaFormatter"/>).</summary>
    [ObservableProperty]
    private string _etaText = EtaFormatter.Unknown;

    /// <summary>Gets the elapsed time, excluding pauses.</summary>
    [ObservableProperty]
    private string _elapsedText = "0:00:00";

    /// <summary>Gets the throughput in source megapixels per second.</summary>
    [ObservableProperty]
    private string _throughputText = "0.00 MP/s";

    /// <summary>Gets the most recent job error, or <see langword="null"/>.</summary>
    [ObservableProperty]
    private string? _lastError;

    /// <summary>Gets the last run's summary, or <see langword="null"/> before the first run ended.</summary>
    [ObservableProperty]
    private string? _lastSummary;

    /// <summary>Gets the GPU adapter the memory bar reads.</summary>
    [ObservableProperty]
    private string _gpuAdapterText = "GPU: detecting…";

    /// <summary>Gets this process's video-memory usage as a percentage of its DXGI budget.</summary>
    [ObservableProperty]
    private double _gpuUsagePercent;

    /// <summary>Gets the video-memory text, for example <c>1.2 / 7.4 GiB (16 %)</c>.</summary>
    [ObservableProperty]
    private string _gpuMemoryText = "unknown";

    /// <summary>Gets a value indicating whether the "open a project on the Scan page" link is shown.</summary>
    public bool ShowNoProject => !HasProject;

    /// <summary>Gets the state as text.</summary>
    public string StateText => State.ToString();

    /// <summary>Gets a value indicating whether no run is active (run options can be changed).</summary>
    public bool IsIdle => State == PipelineState.Idle;

    /// <summary>Applies a progress snapshot (UI thread).</summary>
    /// <param name="progress">The snapshot.</param>
    internal void ApplyProgress(PipelineProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Done = progress.Done;
        Failed = progress.Failed;
        Remaining = progress.Remaining;
        Total = progress.Total;
        ProgressPercent = progress.Total > 0 ? 100.0 * (progress.Done + progress.Failed) / progress.Total : 0;
        CountsText = string.Create(CultureInfo.InvariantCulture, $"{progress.Done:N0} done · {progress.Failed:N0} failed · {progress.Remaining:N0} remaining of {progress.Total:N0}");
        EtaText = EtaFormatter.Format(progress.Eta);
        ElapsedText = FormatElapsed(progress.Elapsed);
        ThroughputText = string.Create(CultureInfo.InvariantCulture, $"{progress.SourceMegapixelsPerSecond:F2} MP/s");
        LastError = progress.LastError;
        foreach (var activity in progress.Stages)
        {
            if ((int)activity.Stage < Lanes.Count)
            {
                Lanes[(int)activity.Stage].Update(activity);
            }
        }

        State = _runner.State;
    }

    /// <summary>Applies a GPU memory reading (UI thread).</summary>
    /// <param name="adapter">The adapter, or <see langword="null"/> when none was found.</param>
    /// <param name="memory">The reading, or <see langword="null"/> when unavailable.</param>
    internal void ApplyGpuMemory(GpuAdapterInfo? adapter, VideoMemoryInfo? memory)
    {
        GpuAdapterText = adapter is null ? "GPU: no adapter found" : $"GPU: {adapter.Name} (device {adapter.DirectMLDeviceId})";
        if (memory is null || memory.Budget == 0)
        {
            GpuUsagePercent = 0;
            GpuMemoryText = "unknown";
            return;
        }

        GpuUsagePercent = Math.Min(100, 100.0 * memory.CurrentUsage / memory.Budget);
        GpuMemoryText = string.Create(CultureInfo.InvariantCulture, $"{memory.CurrentUsage / BytesPerGiB:F2} / {memory.Budget / BytesPerGiB:F2} GiB budget ({GpuUsagePercent:F0} %)");
    }

    /// <summary>Appends log lines, keeping the last <see cref="ForgeRunnerLogSink.Capacity"/> (UI thread).</summary>
    /// <param name="lines">The lines, oldest first.</param>
    internal void AppendLog(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var skip = Math.Max(0, lines.Count - ForgeRunnerLogSink.Capacity);
        for (var i = skip; i < lines.Count; i++)
        {
            LogLines.Add(lines[i]);
        }

        while (LogLines.Count > ForgeRunnerLogSink.Capacity)
        {
            LogLines.RemoveAt(0);
        }
    }

    /// <summary>Formats an elapsed time as <c>h:mm:ss</c>.</summary>
    /// <param name="elapsed">The time.</param>
    /// <returns>The text.</returns>
    internal static string FormatElapsed(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

    /// <summary>Describes a run summary for the "last run" line.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The text.</returns>
    internal static string DescribeSummary(PipelineRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var verdict = summary.Stopped ? "Stopped" : "Finished";
        return string.Create(CultureInfo.InvariantCulture,
            $"{verdict} at {DateTime.Now:HH:mm:ss} after {FormatElapsed(summary.Elapsed)}: {summary.Done:N0} done, {summary.Failed:N0} failed, {summary.Requeued:N0} returned to the queue.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runner.ProgressChanged -= OnRunnerProgress;
        _runner.RunStateChanged -= OnRunStateChanged;
        _session.Changed -= OnSessionChanged;
        _logSink.LinesReceived -= OnLogLines;
        _navigation.Navigated -= OnNavigated;
        lock (_gpuGate)
        {
            _disposed = true;
        }

        UpdateGpuPolling(false);
    }

    /// <summary>Shows the Scan page (the "no project" link).</summary>
    [RelayCommand]
    private void OpenScan() => _navigation.NavigateTo(AppPage.Scan);

    /// <summary>Reloads the installed model families.</summary>
    [RelayCommand]
    private void RefreshModels()
    {
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = _modelCatalog();
        }
        catch (IOException ex)
        {
            LogCatalogFailed(_logger, ex);
            StatusMessage = $"Could not list the installed models: {ex.Message}";
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCatalogFailed(_logger, ex);
            StatusMessage = $"Could not list the installed models: {ex.Message}";
            return;
        }

        var selected = SelectedModel?.Name;
        var families = models.Select(m => GpuProfiler.GetModelFamily(m.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        ModelOptions.Clear();
        ModelOptions.Add(RunModelOption.JobDefault);
        foreach (var family in families)
        {
            ModelOptions.Add(new RunModelOption(family, family));
        }

        SelectedModel = ModelOptions.FirstOrDefault(o => string.Equals(o.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? RunModelOption.JobDefault;
    }

    private bool CanStart() => State == PipelineState.Idle && !IsStarting && HasProject;

    /// <summary>Starts a run with the selected options; completes when the run ends.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        int? limit = null;
        if (LimitEnabled)
        {
            if (JobLimit is not { } value || value < 1)
            {
                StatusMessage = "The job limit must be 1 or greater.";
                return;
            }

            limit = (int)Math.Min(int.MaxValue, decimal.Truncate(value));
        }

        StatusMessage = null;
        IsStarting = true;
        try
        {
            // The run itself is awaited on the thread pool; this method returns to the UI thread only when it ends.
            var summary = await _runner.StartAsync(new ForgeRunRequest(SelectedModel?.Name, limit));
            LastSummary = DescribeSummary(summary);
        }
        catch (Exception ex)
        {
            // Top of a UI command: any start or run failure is logged and shown, never left to crash the app.
            LogStartFailed(_logger, ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsStarting = false;
            State = _runner.State;
        }
    }

    private bool CanPause() => State == PipelineState.Running;

    /// <summary>Pauses the run.</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _runner.Pause();
        State = _runner.State;
    }

    private bool CanResume() => State == PipelineState.Paused;

    /// <summary>Resumes a paused run.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _runner.Unpause();
        State = _runner.State;
    }

    private bool CanStop() => State is PipelineState.Running or PipelineState.Paused;

    /// <summary>Requests a graceful stop.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runner.RequestStop();
        State = _runner.State;
    }

    private void OnRunnerProgress(object? sender, PipelineProgress progress)
    {
        // Coalesce: keep only the newest snapshot and post at most one update at a time.
        Volatile.Write(ref _pendingProgress, progress);
        if (Interlocked.Exchange(ref _progressPosted, 1) == 0)
        {
            _dispatcher.Post(ApplyPendingProgress);
        }
    }

    private void ApplyPendingProgress()
    {
        Volatile.Write(ref _progressPosted, 0);
        if (Interlocked.Exchange(ref _pendingProgress, null) is { } progress)
        {
            ApplyProgress(progress);
        }
    }

    private void OnRunStateChanged(object? sender, PipelineRunSummary? summary) => _dispatcher.Post(() =>
    {
        State = _runner.State;
        if (summary is not null)
        {
            IsStarting = false;
            LastSummary = DescribeSummary(summary);
        }
        else if (State != PipelineState.Idle)
        {
            IsStarting = false;
        }
    });

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        ProjectText = DescribeProject(_session.Current);
        HasProject = _session.Current is not null;
    });

    private void OnNavigated(object? sender, AppPage page) => UpdateGpuPolling(page == AppPage.Run);

    /// <summary>Starts or stops the 1 Hz GPU memory poll.</summary>
    /// <param name="visible">Whether the Run page is shown.</param>
    private void UpdateGpuPolling(bool visible)
    {
        CancellationTokenSource? stop = null;
        lock (_gpuGate)
        {
            var run = visible && !_disposed && _gpuPollInterval is not null;
            if (run && _gpuPolling is null)
            {
                var cts = new CancellationTokenSource();
                _gpuPolling = cts;
                var interval = _gpuPollInterval!.Value;
                _ = Task.Run(() => PollGpuMemoryAsync(_timeProvider, interval, cts.Token));
            }
            else if (!run && _gpuPolling is not null)
            {
                stop = _gpuPolling;
                _gpuPolling = null;
            }
        }

        if (stop is not null)
        {
            stop.Cancel();
            stop.Dispose();
        }
    }

    private void OnLogLines(object? sender, IReadOnlyList<string> lines)
    {
        lock (_logGate)
        {
            _pendingLog.AddRange(lines);
            if (_pendingLog.Count > ForgeRunnerLogSink.Capacity)
            {
                _pendingLog.RemoveRange(0, _pendingLog.Count - ForgeRunnerLogSink.Capacity);
            }
        }

        if (Interlocked.Exchange(ref _logPosted, 1) == 0)
        {
            _dispatcher.Post(ApplyPendingLog);
        }
    }

    private void ApplyPendingLog()
    {
        Volatile.Write(ref _logPosted, 0);
        string[] batch;
        lock (_logGate)
        {
            batch = [.. _pendingLog];
            _pendingLog.Clear();
        }

        if (batch.Length > 0)
        {
            AppendLog(batch);
        }
    }

    private async Task PollGpuMemoryAsync(TimeProvider timeProvider, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);
        var profiler = new GpuProfiler(_adapters);
        GpuAdapterInfo? adapter = null;
        try
        {
            do
            {
                adapter ??= profiler.SelectAdapter(_adapters.GetAdapters());
                var memory = adapter is null ? null : _adapters.QueryVideoMemory(adapter.Luid);
                if (adapter is not null && memory is null)
                {
                    // The adapter may be gone (driver reset, eGPU); select again on the next tick.
                    adapter = null;
                }

                var reported = adapter;
                _dispatcher.Post(() => ApplyGpuMemory(reported, memory));
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page was hidden or the view model disposed: polling ends.
            LogGpuPollingStopped(_logger);
        }
    }

    private static string DescribeProject(ProjectInfo? project) =>
        project is null ? "No project open. Open or create one on the Scan page." : $"Project: {project.Root}  →  {project.OutputRoot}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start the run")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list the installed models")]
    private static partial void LogCatalogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GPU memory polling stopped")]
    private static partial void LogGpuPollingStopped(ILogger logger);
}
