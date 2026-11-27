using System.Diagnostics;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Cli.Pipeline;

/// <summary>
/// A simple <see cref="IForgePipeline"/> that processes queued jobs one at a time through <see cref="TextureProcessor.ProcessAsync"/>.
/// It is the CLI's fallback until the staged <c>ForgePipeline</c> is merged, and stays available as <c>forge --sequential</c> for debugging.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Start: interrupted jobs return to Queued and the work folder is wiped (crash recovery).</item>
/// <item>Pause waits at the processor's safe points (between stages and between tiles); Unpause continues the same item.</item>
/// <item>Stop cancels the current item; it returns to Queued and nothing partial reaches the output folder.</item>
/// <item>A failing job is retried up to <see cref="PipelineOptions.MaxAttempts"/> times in a run, then marked Failed.</item>
/// <item>Progress uses the shared <see cref="PipelineProgress"/> record; the ETA is always <see langword="null"/>.</item>
/// </list>
/// </remarks>
internal sealed partial class SequentialPipeline : IForgePipeline
{
    private static readonly PipelineStage[] AllStages = Enum.GetValues<PipelineStage>();

    private readonly JobStore _store;
    private readonly TextureProcessor _processor;
    private readonly JobMapper _mapper;
    private readonly PipelineOptions _options;
    private readonly int? _maxJobs;
    private readonly TimeProvider _time;
    private readonly ILogger<SequentialPipeline> _logger;
    private readonly PauseTokenSource _pause = new();
    private readonly Stopwatch _active = new();
    private readonly Lock _gate = new();

    private PipelineState _state = PipelineState.Idle;
    private IProgress<PipelineProgress>? _progress;
    private long _lastReport;
    private int _done;
    private int _failed;
    private int _total;
    private double _megapixels;
    private string? _currentPath;
    private PipelineStage _currentStage;
    private TileProgress _tiles;
    private string? _lastError;

    /// <summary>Initializes a new instance of the <see cref="SequentialPipeline"/> class.</summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="processor">Processes one texture.</param>
    /// <param name="mapper">Turns job rows into texture jobs.</param>
    /// <param name="options">Work folder, attempts and progress interval (the worker counts are ignored).</param>
    /// <param name="maxJobs">Process at most this many queued jobs (<c>--limit</c>); <see langword="null"/> processes all.</param>
    /// <param name="time">Clock for progress throttling.</param>
    /// <param name="logger">Logger.</param>
    public SequentialPipeline(
        JobStore store,
        TextureProcessor processor,
        JobMapper mapper,
        PipelineOptions options,
        int? maxJobs = null,
        TimeProvider? time = null,
        ILogger<SequentialPipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(options);
        if (maxJobs is { } max)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, 1, nameof(maxJobs));
        }

        _store = store;
        _processor = processor;
        _mapper = mapper;
        _options = options;
        _maxJobs = maxJobs;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SequentialPipeline>.Instance;
    }

    /// <inheritdoc />
    public PipelineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public async Task<PipelineRunSummary> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken stopToken)
    {
        lock (_gate)
        {
            if (_state != PipelineState.Idle)
            {
                throw new InvalidOperationException("The pipeline is already running.");
            }

            _state = PipelineState.Running;
            _progress = progress;
            _done = _failed = _total = 0;
            _megapixels = 0;
            _currentPath = null;
            _tiles = default;
            _lastError = null;
            _active.Restart();
        }

        using var stopRegistration = stopToken.Register(() => SetState(PipelineState.Stopping));
        var stopped = false;
        var requeued = 0;
        long? runId = null;
        try
        {
            await RecoverAsync().ConfigureAwait(false);

            var jobs = await LoadQueuedAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _total = jobs.Count;
            }

            LogRunStarted(jobs.Count, _maxJobs);
            runId = await _store.StartRunAsync(CancellationToken.None).ConfigureAwait(false);
            Report(force: true);

            foreach (var job in jobs)
            {
                if (stopToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                try
                {
                    await _pause.Token.WaitWhilePausedAsync(stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                if (!await ProcessJobAsync(job, stopToken).ConfigureAwait(false))
                {
                    stopped = true;
                    break;
                }
            }
        }
        finally
        {
            // Stop (or an unexpected error): anything still marked Running goes back to Queued. Never cancel these writes.
            requeued = await _store.RequeueInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
            if (runId is { } id)
            {
                await _store.EndRunAsync(id, _done, _failed, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _active.Stop();
                _currentPath = null;
                _tiles = default;
            }

            _pause.Resume();
            stopped |= stopToken.IsCancellationRequested;
            LogRunEnded(_done, _failed, requeued, stopped, _active.Elapsed.TotalSeconds);

            SetState(PipelineState.Idle);
            Report(force: true);
            lock (_gate)
            {
                _progress = null;
            }
        }

        return new PipelineRunSummary(_done, _failed, requeued, stopped, _active.Elapsed);
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            if (_state != PipelineState.Running)
            {
                return;
            }

            _state = PipelineState.Paused;
            _pause.Pause();
            _active.Stop();
        }

        Report(force: true);
    }

    /// <inheritdoc />
    public void Unpause()
    {
        lock (_gate)
        {
            if (_state != PipelineState.Paused)
            {
                return;
            }

            _state = PipelineState.Running;
            _active.Start();
            _pause.Resume();
        }

        Report(force: true);
    }

    /// <summary>Builds a progress snapshot of the current state.</summary>
    /// <returns>The snapshot.</returns>
    internal PipelineProgress Snapshot()
    {
        lock (_gate)
        {
            var stages = new StageActivity[AllStages.Length];
            for (var i = 0; i < AllStages.Length; i++)
            {
                var stage = AllStages[i];
                var active = _currentPath is not null && stage == _currentStage;
                stages[i] = new StageActivity(
                    stage,
                    active ? _currentPath : null,
                    active ? 1 : 0,
                    0,
                    active && stage == PipelineStage.Upscale ? _tiles.Done : 0,
                    active && stage == PipelineStage.Upscale ? _tiles.Total : 0);
            }

            var seconds = _active.Elapsed.TotalSeconds;
            var remaining = Math.Max(0, _total - _done - _failed);
            return new PipelineProgress(
                _state,
                _done,
                _failed,
                remaining,
                _done + _failed + remaining,
                stages,
                seconds > 0 ? _megapixels / seconds : 0,
                null,
                _active.Elapsed,
                _lastError);
        }
    }

    private async Task RecoverAsync()
    {
        var interrupted = await _store.RequeueInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
        if (interrupted > 0)
        {
            LogRecovered(interrupted);
        }

        if (Directory.Exists(_options.WorkRoot))
        {
            foreach (var entry in new DirectoryInfo(_options.WorkRoot).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    directory.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }

        Directory.CreateDirectory(_options.WorkRoot);
    }

    private async Task<List<JobRecord>> LoadQueuedAsync()
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in _store.GetJobsAsync(JobState.Queued, CancellationToken.None).ConfigureAwait(false))
        {
            jobs.Add(job);
            if (_maxJobs is { } max && jobs.Count >= max)
            {
                break;
            }
        }

        return jobs;
    }

    /// <returns><see langword="false"/> when the run was stopped during this job.</returns>
    private async Task<bool> ProcessJobAsync(JobRecord job, CancellationToken stopToken)
    {
        TextureJob textureJob;
        try
        {
            textureJob = _mapper.Map(job);
        }
        catch (InvalidOperationException ex)
        {
            await FailAsync(job, ex).ConfigureAwait(false);
            return true;
        }

        await _store.MarkRunningAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
        var tiles = new TileReporter(this);
        for (var attempt = 1; ; attempt++)
        {
            SetCurrent(job.Path, PipelineStage.Decode);
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await _processor.ProcessAsync(textureJob, tiles, _pause.Token, stopToken).ConfigureAwait(false);
                await _store.MarkDoneAsync(job.Id, clock.Elapsed.TotalSeconds, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _done++;
                    _megapixels += job.SourceWidth * (double)job.SourceHeight / 1_000_000;
                }

                LogJobDone(job.Path, result.Width, result.Height, clock.Elapsed.TotalSeconds);
                SetCurrent(null, PipelineStage.Decode);
                return true;
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                LogJobAbandoned(job.Path);
                SetCurrent(null, PipelineStage.Decode);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < Math.Max(1, _options.MaxAttempts))
            {
                LogJobRetry(ex, job.Path, attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(job, ex).ConfigureAwait(false);
                SetCurrent(null, PipelineStage.Decode);
                return true;
            }
        }
    }

    private async Task FailAsync(JobRecord job, Exception ex)
    {
        await _store.MarkFailedAsync(job.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _failed++;
            _lastError = $"{job.Path}: {ex.Message}";
        }

        LogJobFailed(ex, job.Path);
        Report(force: true);
    }

    private void SetState(PipelineState state)
    {
        lock (_gate)
        {
            if (_state == state || (state == PipelineState.Stopping && _state == PipelineState.Idle))
            {
                return;
            }

            if (state == PipelineState.Stopping && _state == PipelineState.Paused)
            {
                _active.Start();
            }

            _state = state;
        }

        Report(force: true);
    }

    private void SetCurrent(string? path, PipelineStage stage)
    {
        lock (_gate)
        {
            _currentPath = path;
            _currentStage = stage;
            _tiles = default;
        }

        Report(force: true);
    }

    private void OnTile(TileProgress tile)
    {
        lock (_gate)
        {
            _tiles = tile;
            _currentStage = tile.Total > 0 && tile.Done >= tile.Total ? PipelineStage.PostProcess : PipelineStage.Upscale;
        }

        Report(force: tile.Done >= tile.Total);
    }

    private void Report(bool force)
    {
        IProgress<PipelineProgress>? sink;
        lock (_gate)
        {
            sink = _progress;
            var now = _time.GetTimestamp();
            if (sink is null || (!force && _time.GetElapsedTime(_lastReport, now) < _options.ProgressInterval))
            {
                return;
            }

            _lastReport = now;
        }

        sink.Report(Snapshot());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequential run started: {Count} queued job(s), limit {Limit}")]
    private partial void LogRunStarted(int count, int? limit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run ended: {Done} done, {Failed} failed, {Requeued} requeued, stopped {Stopped}, {Seconds:F1} s")]
    private partial void LogRunEnded(int done, int failed, int requeued, bool stopped, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Crash recovery: {Count} interrupted job(s) returned to Queued")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Done {Path} → {Width}×{Height} in {Seconds:F1} s")]
    private partial void LogJobDone(string path, int width, int height, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped during {Path}; it returns to Queued")]
    private partial void LogJobAbandoned(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attempt {Attempt} of {Path} failed; retrying")]
    private partial void LogJobRetry(Exception exception, string path, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Path} failed")]
    private partial void LogJobFailed(Exception exception, string path);

    /// <summary>Synchronous tile sink (unlike <see cref="Progress{T}"/>, it does not post to the thread pool).</summary>
    private sealed class TileReporter(SequentialPipeline owner) : IProgress<TileProgress>
    {
        public void Report(TileProgress value) => owner.OnTile(value);
    }
}
