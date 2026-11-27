using System.Diagnostics;
using System.Threading.Channels;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// The staged pipeline (IMPLEMENTATION_GUIDE §11): producer → decode (CPU, N) → upscale (GPU, 1) → post-process (CPU, N) →
/// encode + commit (texconv, N), connected by bounded channels.
/// </summary>
/// <remarks>
/// <para><b>Start (crash recovery).</b> Interrupted jobs return to Queued, the work folder is wiped and a run row is opened.</para>
/// <para><b>Jobs.</b> A job is marked Running only when a decode worker takes it, and Done only after the encoder's atomic move
/// into the output folder. A failing job is retried in the same run up to <see cref="PipelineOptions.MaxAttempts"/> times
/// (it stays Running meanwhile), then marked Failed; one bad texture never stops the run.</para>
/// <para><b>Pause.</b> One <see cref="PauseTokenSource"/> is checked by the producer, by every stage before each item and by the
/// upscaler between tiles.</para>
/// <para><b>Stop.</b> Cancelling the stop token makes every stage abandon its current item: its work folder is deleted, nothing
/// reaches the output folder, and the job (still Running) returns to Queued with its attempt count unchanged. Items still waiting
/// in channels are dropped the same way. <see cref="RunAsync"/> then completes normally with <see cref="PipelineRunSummary.Stopped"/> set.</para>
/// </remarks>
public sealed partial class ForgePipeline : IForgePipeline
{
    /// <summary>ETA key for jobs that ran without an AI model (parallax/height maps, jobs without a model).</summary>
    public const string NoModelKey = "lanczos";

    private static readonly TimeSpan ThroughputWindow = TimeSpan.FromSeconds(30);

    private readonly JobStore _store;
    private readonly TextureProcessor _processor;
    private readonly IEtaEstimator _eta;
    private readonly PipelineOptions _options;
    private readonly ILogger<ForgePipeline> _logger;
    private readonly Lock _gate = new();
    private readonly PauseTokenSource _pause = new();
    private readonly Stopwatch _activeClock = new();
    private PipelineState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForgePipeline"/> class.
    /// </summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="processor">Runs the per-texture stages; must use the same work and output folders as <paramref name="options"/>.</param>
    /// <param name="eta">Remaining-time estimator; receives every finished job.</param>
    /// <param name="options">Pipeline tuning.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    /// <exception cref="ArgumentException">The folders do not match the processor's, the work folder is a drive root, or the work and output folders overlap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A worker count, capacity, attempt count or interval is not positive.</exception>
    public ForgePipeline(JobStore store, TextureProcessor processor, IEtaEstimator eta, PipelineOptions options, ILogger<ForgePipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(eta);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DecodeWorkers, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PostProcessWorkers, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.EncodeWorkers, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InputCapacity, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.StageCapacity, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ProgressInterval, TimeSpan.Zero, nameof(options));
        if (options.MaxJobs is { } maxJobs)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxJobs, 1, nameof(options));
        }
        ValidateFolders(options, processor);

        _store = store;
        _processor = processor;
        _eta = eta;
        _options = options;
        _logger = logger ?? NullLogger<ForgePipeline>.Instance;
    }

    /// <summary>
    /// Gets the normal-map strength for a rule group (the job table stores only the group name). When <see langword="null"/>,
    /// or for jobs without a group, the strength is 1.0.
    /// </summary>
    /// <remarks>Kept for compatibility; <see cref="LookForGroup"/> wins when it returns a look.</remarks>
    public Func<string, double>? NormalStrengthForGroup { get; init; }

    /// <summary>
    /// Gets the effective look (§8) for a rule group, for example <c>name =&gt; rules.Groups.First(...).EffectiveLook</c>. When it is
    /// <see langword="null"/> or returns <see langword="null"/>, the job gets the neutral look with <see cref="NormalStrengthForGroup"/>'s
    /// strength. The stable hash of the job's effective look for its kind (<see cref="LookSettings.GetStableHash(TextureKind)"/>) is
    /// stored on the job when it is marked Done.
    /// </summary>
    public Func<string, LookSettings?>? LookForGroup { get; init; }

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
    /// <exception cref="InvalidOperationException">A run is already in progress.</exception>
    public async Task<PipelineRunSummary> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken stopToken)
    {
        lock (_gate)
        {
            if (_state != PipelineState.Idle)
            {
                throw new InvalidOperationException("A pipeline run is already in progress.");
            }

            _state = PipelineState.Running;
            _pause.Resume();
            _activeClock.Restart();
        }

        var run = new RunContext();
        long? runId = null;
        var requeued = 0;
        try
        {
            using var registration = stopToken.Register(OnStopRequested);

            // Crash recovery: bookkeeping uses CancellationToken.None so a stop never leaves it half done.
            var recovered = await _store.RequeueInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
            WipeWorkRoot();
            runId = await _store.StartRunAsync(CancellationToken.None).ConfigureAwait(false);

            _eta.Seed(NoModelKey, _options.NoModelSecondsPerMegapixel);

            var jobs = new List<JobRecord>();
            await foreach (var job in _store.GetJobsAsync(JobState.Queued, CancellationToken.None).ConfigureAwait(false))
            {
                if (_options.MaxJobs is { } maxJobs && jobs.Count >= maxJobs)
                {
                    break;
                }

                jobs.Add(job);
                run.AddRemaining(job.Id, EtaKeyOf(job, _options.ModelOverride), MegapixelsOf(job));
            }

            run.Total = jobs.Count;
            LogRunStarted(jobs.Count, recovered);

            if (!stopToken.IsCancellationRequested && jobs.Count > 0)
            {
                await ExecuteAsync(run, jobs, progress, stopToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                // Committed outputs whose Done mark failed get one more chance before anything is requeued, so a committed
                // texture is not re-encoded as new work by the next run.
                await FlushUnrecordedDoneAsync(run).ConfigureAwait(false);

                // Abandoned jobs are still Running; return them to Queued (attempts unchanged), then close the run row.
                requeued = await _store.RequeueInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
                if (runId is { } id)
                {
                    await _store.EndRunAsync(id, run.Done, run.Failed, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                TimeSpan elapsedAtEnd;
                lock (_gate)
                {
                    _activeClock.Stop();
                    _pause.Resume();
                    _state = PipelineState.Idle;
                    elapsedAtEnd = _activeClock.Elapsed;
                }

                if (progress is not null)
                {
                    ReportSafely(progress, Snapshot(run, PipelineState.Idle, elapsedAtEnd));
                }
            }
        }

        var elapsed = ActiveElapsed();
        var stopped = stopToken.IsCancellationRequested;
        LogRunEnded(run.Done, run.Failed, requeued, stopped, elapsed.TotalSeconds);

        return new PipelineRunSummary(run.Done, run.Failed, requeued, stopped, elapsed);
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            if (_state == PipelineState.Running)
            {
                _pause.Pause();
                _activeClock.Stop();
                _state = PipelineState.Paused;
            }
        }
    }

    /// <inheritdoc />
    public void Unpause()
    {
        lock (_gate)
        {
            if (_state == PipelineState.Paused)
            {
                _pause.Resume();
                _activeClock.Start();
                _state = PipelineState.Running;
            }
        }
    }

    /// <summary>
    /// Maps a job row to a <see cref="TextureJob"/>: the target size keeps the source aspect ratio with the longer side at
    /// <see cref="JobRecord.Target"/> (rounded like the rule evaluator).
    /// </summary>
    /// <param name="record">The job row.</param>
    /// <param name="normalStrengthForGroup">Optional group → normal strength lookup (used when there is no look).</param>
    /// <param name="lookForGroup">Optional group → effective look lookup; a returned look wins over the normal strength lookup.</param>
    /// <returns>The texture job. <see cref="TextureJob.SourceDxgiFormat"/> is left null; the decode stage reads it from the DDS header.</returns>
    /// <exception cref="InvalidDataException">The kind is unknown, or the source or target size is missing.</exception>
    internal static TextureJob ToTextureJob(JobRecord record, Func<string, double>? normalStrengthForGroup = null, Func<string, LookSettings?>? lookForGroup = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!Enum.TryParse<TextureKind>(record.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || int.TryParse(record.Kind, out _))
        {
            throw new InvalidDataException($"Unknown texture kind '{record.Kind}'.");
        }

        if (record.SourceWidth < 1 || record.SourceHeight < 1)
        {
            throw new InvalidDataException($"Source size {record.SourceWidth}×{record.SourceHeight} is unknown.");
        }

        if (record.Target < 1)
        {
            throw new InvalidDataException($"Target size {record.Target} is not positive.");
        }

        var sourceMax = Math.Max(record.SourceWidth, record.SourceHeight);
        var look = record.Group is { } lookGroup && lookForGroup is not null ? lookForGroup(lookGroup) : null;
        var strength = look?.NormalStrength
            ?? (record.Group is { } group && normalStrengthForGroup is not null ? normalStrengthForGroup(group) : 1.0);

        return new TextureJob(
            record.Path,
            record.Archive,
            kind,
            Scale(record.SourceWidth, record.Target, sourceMax),
            Scale(record.SourceHeight, record.Target, sourceMax),
            record.Model ?? string.Empty,
            strength,
            Look: look);
    }

    private static int Scale(int dimension, int targetMax, int sourceMax) =>
        (int)Math.Max(1, Math.Round((double)dimension * targetMax / sourceMax, MidpointRounding.AwayFromZero));

    private static double MegapixelsOf(JobRecord record) => Math.Max(0, record.SourceWidth) * (double)Math.Max(0, record.SourceHeight) / 1_000_000d;

    // The ETA key must be the model that actually runs (the override when set), or the remaining jobs keep a key that never gets a rate.
    private static string EtaKeyOf(JobRecord record, string? modelOverride)
    {
        if (string.Equals(record.Kind, nameof(TextureKind.Parallax), StringComparison.OrdinalIgnoreCase))
        {
            return NoModelKey;
        }

        var model = string.IsNullOrEmpty(modelOverride) ? record.Model : modelOverride;
        return string.IsNullOrEmpty(model) ? NoModelKey : model;
    }

    private static string FullPathWithSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    private static void ValidateFolders(PipelineOptions options, TextureProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkRoot, nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputRoot, nameof(options));

        var work = FullPathWithSeparator(options.WorkRoot);
        var output = FullPathWithSeparator(options.OutputRoot);
        if (!string.Equals(work, FullPathWithSeparator(processor.WorkRoot), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(output, FullPathWithSeparator(processor.OutputRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The pipeline's work and output folders must be the processor's.", nameof(options));
        }

        if (string.Equals(Path.GetPathRoot(work), work, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The work folder '{options.WorkRoot}' must not be a drive root: it is wiped on start.", nameof(options));
        }

        if (output.StartsWith(work, StringComparison.OrdinalIgnoreCase) || work.StartsWith(output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The work folder and the output folder must not contain each other.", nameof(options));
        }
    }

    private static void DrainAndAbandon(ChannelReader<WorkItem> reader, RunContext run)
    {
        while (reader.TryRead(out var item))
        {
            run.Abandon(item);
        }
    }

    private static BoundedChannelOptions Bounded(int capacity, bool singleWriter = false, bool singleReader = false) =>
        new(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = singleWriter, SingleReader = singleReader };

    private async Task ExecuteAsync(RunContext run, List<JobRecord> jobs, IProgress<PipelineProgress>? progress, CancellationToken stopToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        using var reporterCts = new CancellationTokenSource();
        var ct = runCts.Token;

        var input = Channel.CreateBounded<WorkItem>(Bounded(_options.InputCapacity, singleWriter: true));
        var decoded = Channel.CreateBounded<WorkItem>(Bounded(_options.StageCapacity));
        var upscaled = Channel.CreateBounded<WorkItem>(Bounded(_options.StageCapacity, singleWriter: true));
        var processed = Channel.CreateBounded<WorkItem>(Bounded(_options.StageCapacity));
        // Unbounded on purpose, and still bounded in practice: an item enters it only by leaving a stage after a failure, so it
        // never holds more than the items in flight (InputCapacity + 3 × StageCapacity + workers + 1). A bounded channel here
        // would deadlock: a failing encode worker would block on it while the producer blocks on the full input channel.
        var retries = Channel.CreateUnbounded<WorkItem>();
        run.Retries = retries.Writer;
        run.Stage(PipelineStage.Decode).QueuedCount = () => input.Reader.Count;
        run.Stage(PipelineStage.Upscale).QueuedCount = () => decoded.Reader.Count;
        run.Stage(PipelineStage.PostProcess).QueuedCount = () => upscaled.Reader.Count;
        run.Stage(PipelineStage.Encode).QueuedCount = () => processed.Reader.Count;

        var reporter = progress is null ? Task.CompletedTask : ReportLoopAsync(run, progress, reporterCts.Token);
        try
        {
            var producer = Task.Run(() => GuardAsync(() => ProduceAsync(run, jobs, input.Writer, retries.Reader, ct), runCts), CancellationToken.None);
            var stages = new[]
            {
                RunStageAsync(_options.DecodeWorkers, () => DecodeWorkerAsync(run, input.Reader, decoded.Writer, ct), decoded.Writer, runCts),
                RunStageAsync(1, () => UpscaleWorkerAsync(run, decoded.Reader, upscaled.Writer, ct), upscaled.Writer, runCts),
                RunStageAsync(_options.PostProcessWorkers, () => PostProcessWorkerAsync(run, upscaled.Reader, processed.Writer, ct), processed.Writer, runCts),
                RunStageAsync(_options.EncodeWorkers, () => EncodeWorkerAsync(run, processed.Reader, ct), null, runCts),
            };

            await Task.WhenAll([producer, .. stages]).ConfigureAwait(false);
        }
        finally
        {
            // Anything still queued between stages is abandoned: its work folder is deleted and its job returns to Queued.
            DrainAndAbandon(input.Reader, run);
            DrainAndAbandon(decoded.Reader, run);
            DrainAndAbandon(upscaled.Reader, run);
            DrainAndAbandon(processed.Reader, run);
            retries.Writer.TryComplete();
            DrainAndAbandon(retries.Reader, run);

            await reporterCts.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }
    }

    private async Task RunStageAsync(int workers, Func<Task> worker, ChannelWriter<WorkItem>? next, CancellationTokenSource runCts)
    {
        try
        {
            var tasks = new Task[workers];
            for (var i = 0; i < workers; i++)
            {
                tasks[i] = Task.Run(() => GuardAsync(worker, runCts), CancellationToken.None);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            next?.TryComplete();
        }
    }

    /// <summary>Runs a producer or worker; a stop ends it quietly, an unexpected fault stops the whole run and propagates.</summary>
    private async Task GuardAsync(Func<Task> body, CancellationTokenSource runCts)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // Stop requested (or another worker faulted): the current item was already abandoned by the worker.
        }
        catch (Exception ex)
        {
            LogWorkerFaulted(ex);
            await runCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProduceAsync(RunContext run, List<JobRecord> jobs, ChannelWriter<WorkItem> input, ChannelReader<WorkItem> retries, CancellationToken ct)
    {
        var pause = _pause.Token;
        try
        {
            foreach (var record in jobs)
            {
                while (retries.TryRead(out var retry))
                {
                    await WriteOrAbandonAsync(run, input, retry, ct).ConfigureAwait(false);
                }

                await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);

                TextureJob job;
                try
                {
                    job = ToTextureJob(record, NormalStrengthForGroup, LookForGroup);
                    if (_options.ModelOverride is { Length: > 0 } modelOverride)
                    {
                        job = job with { Model = modelOverride };
                    }

                    _processor.GetOutputPath(job.Path);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                {
                    // A broken row (bad kind or size, or a path outside the output folder) cannot succeed on retry: fail it
                    // without decoding and without counting an attempt.
                    LogJobFailed(ex, record.Path, ex.Message);
                    await _store.MarkFailedAsync(record.Id, ex.Message, 0, CancellationToken.None).ConfigureAwait(false);
                    run.RecordFailure(record.Id, $"{record.Path}: {ex.Message}");
                    continue;
                }

                var item = new WorkItem(record, job, EtaKeyOf(record, _options.ModelOverride), MegapixelsOf(record));
                run.Admit();
                await WriteOrAbandonAsync(run, input, item, ct).ConfigureAwait(false);
            }

            run.FinishProducing();
            await foreach (var retry in retries.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                await WriteOrAbandonAsync(run, input, retry, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            input.TryComplete();
        }
    }

    private static async Task WriteOrAbandonAsync(RunContext run, ChannelWriter<WorkItem> writer, WorkItem item, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            run.Abandon(item);
            throw;
        }
    }

    private async Task DecodeWorkerAsync(RunContext run, ChannelReader<WorkItem> input, ChannelWriter<WorkItem> output, CancellationToken ct)
    {
        var pause = _pause.Token;
        var tracker = run.Stage(PipelineStage.Decode);
        while (await input.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (!input.TryRead(out var item))
            {
                continue;
            }

            if (!item.Taken)
            {
                // Take the job only if it is still Queued: a rescan during the run (for example "now provided by a mod") wins.
                if (!await _store.TryMarkRunningAsync(item.Record.Id, CancellationToken.None).ConfigureAwait(false))
                {
                    LogJobNoLongerQueued(item.Record.Path);
                    run.RecordNotTaken(item);
                    continue;
                }

                item.Taken = true;
            }

            var succeeded = await ProcessItemAsync(run, tracker, item, async () =>
            {
                item.Texture = await _processor.DecodeAsync(item.Job, pause, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (succeeded)
            {
                await WriteOrAbandonAsync(run, output, item, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task UpscaleWorkerAsync(RunContext run, ChannelReader<WorkItem> input, ChannelWriter<WorkItem> output, CancellationToken ct)
    {
        var pause = _pause.Token;
        var tracker = run.Stage(PipelineStage.Upscale);
        while (await input.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (!input.TryRead(out var item))
            {
                continue;
            }

            var succeeded = await ProcessItemAsync(run, tracker, item, async () =>
            {
                tracker.Report(new Imaging.TileProgress(0, 0));
                await _processor.UpscaleAsync(item.Texture!, tracker, pause, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (succeeded)
            {
                await WriteOrAbandonAsync(run, output, item, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PostProcessWorkerAsync(RunContext run, ChannelReader<WorkItem> input, ChannelWriter<WorkItem> output, CancellationToken ct)
    {
        var pause = _pause.Token;
        var tracker = run.Stage(PipelineStage.PostProcess);
        while (await input.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (!input.TryRead(out var item))
            {
                continue;
            }

            var succeeded = await ProcessItemAsync(run, tracker, item, () =>
            {
                TextureProcessor.PostProcess(item.Texture!);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
            if (succeeded)
            {
                await WriteOrAbandonAsync(run, output, item, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task EncodeWorkerAsync(RunContext run, ChannelReader<WorkItem> input, CancellationToken ct)
    {
        var pause = _pause.Token;
        var tracker = run.Stage(PipelineStage.Encode);
        while (await input.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (!input.TryRead(out var item))
            {
                continue;
            }

            TextureResult? result = null;
            var committed = await ProcessItemAsync(run, tracker, item, async () =>
            {
                result = await _processor.EncodeAsync(item.Texture!, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (committed && result is not null)
            {
                // Only the encoder's atomic move makes the item count as finished (§10 step 9), so from here on the job is done
                // whatever happens to the bookkeeping: it is never retried or re-encoded as new work.
                try
                {
                    var lookHash = item.Job.EffectiveLook.GetStableHash(item.Job.Kind);
                    if (!await TryMarkDoneAsync(item.Record.Id, item.Record.Path, result.Timings.Total.TotalSeconds, lookHash).ConfigureAwait(false))
                    {
                        run.AddUnrecordedDone(item.Record.Id, item.Record.Path, result.Timings.Total.TotalSeconds, lookHash);
                    }
                }
                finally
                {
                    item.Texture?.Dispose();
                    item.Texture = null;
                }

                run.RecordDone(item, result.Timings.Total, ActiveElapsed());
                _eta.Record(result.UsedModel ? item.Job.Model : NoModelKey, item.SourceMegapixels, result.Timings.Total);
            }
        }
    }

    /// <summary>
    /// Runs one stage step for <paramref name="item"/>. A stop abandons the item and rethrows; any other exception is a job failure
    /// (retried or marked Failed).
    /// </summary>
    /// <returns><see langword="true"/> when the step succeeded and the item moves on.</returns>
    private async Task<bool> ProcessItemAsync(RunContext run, StageTracker tracker, WorkItem item, Func<Task> step, CancellationToken ct)
    {
        tracker.Enter(item.Record.Path);
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Abandon(item);
            throw;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(run, item, ex, ct).ConfigureAwait(false);
            return false;
        }
        finally
        {
            tracker.Exit(item.Record.Path);
        }

        return true;
    }

    private async Task HandleFailureAsync(RunContext run, WorkItem item, Exception exception, CancellationToken ct)
    {
        item.Texture?.Dispose();
        item.Texture = null;
        var path = item.Record.Path;

        if (ct.IsCancellationRequested)
        {
            // A failure during a stop (for example texconv killed by the stop) says nothing about the texture, on any attempt:
            // requeue it instead of failing it.
            run.Abandon(item);
            return;
        }

        if (item.Attempt < _options.MaxAttempts)
        {
            LogJobRetry(exception, path, item.Attempt, _options.MaxAttempts, exception.Message);
            run.LastError = $"{path}: {exception.Message}";
            item.Attempt++;
            if (run.Retries!.TryWrite(item))
            {
                return;
            }

            run.Abandon(item);
            return;
        }

        LogJobFailed(exception, path, exception.Message);
        await _store.MarkFailedAsync(item.Record.Id, exception.Message, item.Attempt, CancellationToken.None).ConfigureAwait(false);
        run.RecordFailure(item.Record.Id, $"{path}: {exception.Message}");
        run.Release();
    }

    /// <summary>Marks a committed job Done, retrying a few times; a final failure is logged loudly and reported as false.</summary>
    private async Task<bool> TryMarkDoneAsync(long id, string path, double seconds, string lookHash)
    {
        const int MaxTries = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _store.MarkDoneAsync(id, seconds, lookHash, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (attempt < MaxTries)
            {
                LogMarkDoneRetry(ex, path, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMarkDoneFailed(ex, path);
                return false;
            }
        }
    }

    /// <summary>Gives committed outputs whose Done mark failed during the run a last chance before jobs are requeued.</summary>
    private async Task FlushUnrecordedDoneAsync(RunContext run)
    {
        foreach (var (id, path, seconds, lookHash) in run.TakeUnrecordedDone())
        {
            await TryMarkDoneAsync(id, path, seconds, lookHash).ConfigureAwait(false);
        }
    }

    private void ReportSafely(IProgress<PipelineProgress> progress, PipelineProgress snapshot)
    {
        try
        {
            progress.Report(snapshot);
        }
        catch (Exception ex)
        {
            // A broken progress handler must not fault the run or hide a worker's exception.
            LogProgressHandlerFailed(ex);
        }
    }

    private async Task ReportLoopAsync(RunContext run, IProgress<PipelineProgress> progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                PipelineState state;
                TimeSpan elapsed;
                lock (_gate)
                {
                    (state, elapsed) = (_state, _activeClock.Elapsed);
                }

                ReportSafely(progress, Snapshot(run, state, elapsed));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The run has ended; the caller sends the final report.
        }
    }

    private PipelineProgress Snapshot(RunContext run, PipelineState state, TimeSpan elapsed)
    {
        var (done, failed, total) = (run.Done, run.Failed, run.Total);
        var remaining = Math.Max(0, total - done - failed);
        var stages = run.Stages.Select(s => s.Activity()).ToArray();
        var rate = run.Throughput(elapsed);

        TimeSpan? eta = TimeSpan.Zero;
        if (remaining > 0)
        {
            var maxParallelism = _options.DecodeWorkers + 1 + _options.PostProcessWorkers + _options.EncodeWorkers;
            var parallelism = elapsed.TotalSeconds >= 1 ? Math.Clamp(run.BusySeconds / elapsed.TotalSeconds, 1, maxParallelism) : 1;
            eta = _eta.Estimate(run.RemainingByModel(), parallelism);
        }

        return new PipelineProgress(state, done, failed, remaining, done + failed + remaining, stages, rate, eta, elapsed, run.LastError);
    }

    private TimeSpan ActiveElapsed()
    {
        lock (_gate)
        {
            return _activeClock.Elapsed;
        }
    }

    private void OnStopRequested()
    {
        lock (_gate)
        {
            if (_state is PipelineState.Running or PipelineState.Paused)
            {
                _state = PipelineState.Stopping;
            }
        }
    }

    private void WipeWorkRoot()
    {
        // Leftover staging files on the output volume are never committed outputs: remove the whole staging folder.
        var staging = _processor.StagingRoot;
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (IOException ex)
        {
            LogWipeFailed(ex, staging);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogWipeFailed(ex, staging);
        }

        var root = _options.WorkRoot;
        Directory.CreateDirectory(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (IOException ex)
            {
                LogWipeFailed(ex, entry);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogWipeFailed(ex, entry);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run started: {Queued} queued jobs ({Recovered} recovered from an interrupted run)")]
    private partial void LogRunStarted(int queued, int recovered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run ended: {Done} done, {Failed} failed, {Requeued} requeued, stopped {Stopped}, {Seconds:F1} s")]
    private partial void LogRunEnded(int done, int failed, int requeued, bool stopped, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} failed (attempt {Attempt} of {MaxAttempts}), retrying: {Error}")]
    private partial void LogJobRetry(Exception exception, string path, int attempt, int maxAttempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Path} failed: {Error}")]
    private partial void LogJobFailed(Exception exception, string path, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "A pipeline worker faulted; stopping the run")]
    private partial void LogWorkerFaulted(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Entry} while cleaning temporary folders")]
    private partial void LogWipeFailed(Exception exception, string entry);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Path} is no longer queued (changed by a rescan); skipped in this run")]
    private partial void LogJobNoLongerQueued(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} is committed but marking it Done failed (try {Attempt}); retrying")]
    private partial void LogMarkDoneRetry(Exception exception, string path, int attempt);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Path} is committed to the output folder but could not be marked Done in the job store")]
    private partial void LogMarkDoneFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The progress handler threw; the run continues")]
    private partial void LogProgressHandlerFailed(Exception exception);

    /// <summary>One job travelling through the stages.</summary>
    private sealed class WorkItem(JobRecord record, TextureJob job, string etaModel, double sourceMegapixels)
    {
        public JobRecord Record { get; } = record;

        public TextureJob Job { get; } = job;

        public string EtaModel { get; } = etaModel;

        public double SourceMegapixels { get; } = sourceMegapixels;

        public int Attempt { get; set; } = 1;

        /// <summary>Gets or sets a value indicating whether this run marked the job Running (it stays Running across retries).</summary>
        public bool Taken { get; set; }

        public DecodedTexture? Texture { get; set; }
    }

    /// <summary>Activity of one stage; also the upscaler's tile progress sink.</summary>
    private sealed class StageTracker(PipelineStage stage) : IProgress<Imaging.TileProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _paths = [];
        private int _tilesDone;
        private int _tilesTotal;

        public Func<int>? QueuedCount { get; set; }

        public void Enter(string path)
        {
            lock (_gate)
            {
                _paths.Add(path);
            }
        }

        public void Exit(string path)
        {
            lock (_gate)
            {
                _paths.Remove(path);
                if (_paths.Count == 0)
                {
                    _tilesDone = 0;
                    _tilesTotal = 0;
                }
            }
        }

        public void Report(Imaging.TileProgress value)
        {
            lock (_gate)
            {
                _tilesDone = value.Done;
                _tilesTotal = value.Total;
            }
        }

        public StageActivity Activity()
        {
            lock (_gate)
            {
                var current = _paths.Count == 0 ? null : _paths[^1];
                return new StageActivity(stage, current, _paths.Count, QueuedCount?.Invoke() ?? 0, _tilesDone, _tilesTotal);
            }
        }
    }

    /// <summary>Counters and bookkeeping of one run; thread-safe.</summary>
    private sealed class RunContext
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, (string Model, double Megapixels)> _remaining = [];
        private readonly Dictionary<string, (int Count, double Megapixels)> _remainingByModel = new(StringComparer.Ordinal);
        private readonly Queue<(TimeSpan At, double Megapixels)> _recent = new();
        private readonly List<(long Id, string Path, double Seconds, string LookHash)> _unrecordedDone = [];
        private int _total;
        private int _done;
        private int _failed;
        private int _outstanding;
        private int _producerDone;
        private double _busySeconds;
        private string? _lastError;

        public RunContext()
        {
            Stages = [.. Enum.GetValues<PipelineStage>().Select(s => new StageTracker(s))];
        }

        public IReadOnlyList<StageTracker> Stages { get; }

        public ChannelWriter<WorkItem>? Retries { get; set; }

        public int Total
        {
            get => Volatile.Read(ref _total);
            set => Volatile.Write(ref _total, value);
        }

        public int Done => Volatile.Read(ref _done);

        public int Failed => Volatile.Read(ref _failed);

        public string? LastError
        {
            get => Volatile.Read(ref _lastError);
            set => Volatile.Write(ref _lastError, value);
        }

        public double BusySeconds
        {
            get
            {
                lock (_gate)
                {
                    return _busySeconds;
                }
            }
        }

        public StageTracker Stage(PipelineStage stage) => Stages[(int)stage];

        public void AddRemaining(long id, string model, double megapixels)
        {
            lock (_gate)
            {
                RemoveRemaining(id);
                _remaining[id] = (model, megapixels);
                var (count, sum) = _remainingByModel.GetValueOrDefault(model);
                _remainingByModel[model] = (count + 1, sum + megapixels);
            }
        }

        /// <summary>Gets the remaining source megapixels per model; O(models), from sums kept up to date as jobs finish.</summary>
        /// <returns>One entry per model with remaining work (seconds-per-megapixel estimates are linear, so sums suffice).</returns>
        public IEnumerable<(string Model, double SourceMegapixels)> RemainingByModel()
        {
            lock (_gate)
            {
                return [.. _remainingByModel.Select(kv => (kv.Key, kv.Value.Megapixels))];
            }
        }

        /// <summary>The job is no longer Queued in the database (a rescan changed it): it leaves this run without a result.</summary>
        public void RecordNotTaken(WorkItem item)
        {
            lock (_gate)
            {
                RemoveRemaining(item.Record.Id);
            }

            Interlocked.Decrement(ref _total);
            Abandon(item);
        }

        public void AddUnrecordedDone(long id, string path, double seconds, string lookHash)
        {
            lock (_gate)
            {
                _unrecordedDone.Add((id, path, seconds, lookHash));
            }
        }

        public (long Id, string Path, double Seconds, string LookHash)[] TakeUnrecordedDone()
        {
            lock (_gate)
            {
                var pending = _unrecordedDone.ToArray();
                _unrecordedDone.Clear();
                return pending;
            }
        }

        public void Admit() => Interlocked.Increment(ref _outstanding);

        public void FinishProducing()
        {
            Interlocked.Exchange(ref _producerDone, 1);
            if (Volatile.Read(ref _outstanding) == 0)
            {
                Retries?.TryComplete();
            }
        }

        /// <summary>An admitted item reached a final state in this run (done, failed or abandoned).</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0 && Volatile.Read(ref _producerDone) == 1)
            {
                Retries?.TryComplete();
            }
        }

        public void Abandon(WorkItem item)
        {
            item.Texture?.Dispose();
            item.Texture = null;
            Release();
        }

        public void RecordDone(WorkItem item, TimeSpan busy, TimeSpan at)
        {
            lock (_gate)
            {
                RemoveRemaining(item.Record.Id);
                _busySeconds += busy.TotalSeconds;
                _recent.Enqueue((at, item.SourceMegapixels));
            }

            Interlocked.Increment(ref _done);
            Release();
        }

        public void RecordFailure(long id, string error)
        {
            lock (_gate)
            {
                RemoveRemaining(id);
            }

            LastError = error;
            Interlocked.Increment(ref _failed);
        }

        /// <summary>Removes a job from the remaining set and its model's sum; the caller holds the lock.</summary>
        private void RemoveRemaining(long id)
        {
            if (!_remaining.Remove(id, out var entry))
            {
                return;
            }

            var (count, megapixels) = _remainingByModel[entry.Model];
            if (count <= 1)
            {
                // Drop a model with no jobs left, so the ETA never waits on a model that has no work.
                _remainingByModel.Remove(entry.Model);
            }
            else
            {
                _remainingByModel[entry.Model] = (count - 1, Math.Max(0, megapixels - entry.Megapixels));
            }
        }

        public double Throughput(TimeSpan now)
        {
            lock (_gate)
            {
                while (_recent.Count > 0 && now - _recent.Peek().At > ThroughputWindow)
                {
                    _recent.Dequeue();
                }

                var span = now < ThroughputWindow ? now : ThroughputWindow;
                return span.TotalSeconds <= 0 ? 0 : _recent.Sum(r => r.Megapixels) / span.TotalSeconds;
            }
        }
    }
}
