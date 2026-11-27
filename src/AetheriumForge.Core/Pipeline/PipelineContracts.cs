namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Pipeline lifecycle (IMPLEMENTATION_GUIDE §14, Run screen state machine): Idle → Running ⇄ Paused → Stopping → Idle.
/// </summary>
public enum PipelineState
{
    /// <summary>Not running.</summary>
    Idle,

    /// <summary>Processing jobs.</summary>
    Running,

    /// <summary>Paused: stages wait at their next safe point (between items, and between tiles in the upscaler).</summary>
    Paused,

    /// <summary>Stop requested: stages finish or abandon their current item; abandoned items return to Queued.</summary>
    Stopping,
}

/// <summary>
/// Pipeline stages, in order (IMPLEMENTATION_GUIDE §11).
/// </summary>
public enum PipelineStage
{
    /// <summary>Reading the source from its archive and decoding it with texconv (CPU).</summary>
    Decode,

    /// <summary>AI upscaling on the GPU (one worker per adapter).</summary>
    Upscale,

    /// <summary>Resize, normal renormalisation and alpha merge (CPU).</summary>
    PostProcess,

    /// <summary>BC7 encode with texconv, atomic move into the output folder, and marking the job Done.</summary>
    Encode,
}

/// <summary>
/// What one stage is doing right now; one entry per stage in <see cref="PipelineProgress.Stages"/>.
/// </summary>
/// <param name="Stage">The stage.</param>
/// <param name="CurrentPath">Path of an item currently in this stage, or <see langword="null"/> when idle.</param>
/// <param name="InFlight">Items currently inside this stage.</param>
/// <param name="Queued">Items waiting in this stage's input channel.</param>
/// <param name="TilesDone">For <see cref="PipelineStage.Upscale"/>: tiles finished in the current image.</param>
/// <param name="TilesTotal">For <see cref="PipelineStage.Upscale"/>: tiles in the current image.</param>
public readonly record struct StageActivity(PipelineStage Stage, string? CurrentPath, int InFlight, int Queued, int TilesDone, int TilesTotal);

/// <summary>
/// A progress snapshot, emitted at most every <see cref="PipelineOptions.ProgressInterval"/> (about 10 Hz).
/// </summary>
/// <param name="State">Pipeline state.</param>
/// <param name="Done">Jobs finished in this run.</param>
/// <param name="Failed">Jobs that failed in this run.</param>
/// <param name="Remaining">Queued jobs not yet finished (including those in flight).</param>
/// <param name="Total">Done + Failed + Remaining at the time of the snapshot.</param>
/// <param name="Stages">One activity entry per <see cref="PipelineStage"/>, in order.</param>
/// <param name="SourceMegapixelsPerSecond">Recent throughput in source megapixels per second.</param>
/// <param name="Eta">Estimated time to finish the remaining jobs, or <see langword="null"/> when unknown.</param>
/// <param name="Elapsed">Wall-clock time of this run, excluding time spent paused.</param>
/// <param name="LastError">The most recent job error message, or <see langword="null"/>.</param>
public sealed record PipelineProgress(
    PipelineState State,
    int Done,
    int Failed,
    int Remaining,
    int Total,
    IReadOnlyList<StageActivity> Stages,
    double SourceMegapixelsPerSecond,
    TimeSpan? Eta,
    TimeSpan Elapsed,
    string? LastError);

/// <summary>
/// Pipeline tuning (IMPLEMENTATION_GUIDE §11). Defaults follow the guide.
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>Gets the folder for temporary files; wiped at start (crash recovery).</summary>
    public required string WorkRoot { get; init; }

    /// <summary>Gets the mod output folder; textures go to <c>&lt;OutputRoot&gt;\textures\...</c>.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Gets the decode workers (CPU).</summary>
    public int DecodeWorkers { get; init; } = Math.Max(1, Environment.ProcessorCount / 4);

    /// <summary>Gets the post-process workers (CPU).</summary>
    public int PostProcessWorkers { get; init; } = Math.Max(1, Environment.ProcessorCount / 4);

    /// <summary>Gets the encode workers (texconv processes).</summary>
    public int EncodeWorkers { get; init; } = 2;

    /// <summary>Gets the capacity of the producer → decode channel.</summary>
    public int InputCapacity { get; init; } = 8;

    /// <summary>Gets the capacity of each channel between later stages.</summary>
    public int StageCapacity { get; init; } = 4;

    /// <summary>Gets how many times a failing job is attempted in one run before it is marked Failed.</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>Gets the minimum interval between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Runs queued jobs through the staged pipeline (IMPLEMENTATION_GUIDE §11). Implemented by <c>ForgePipeline</c>:
/// <c>ForgePipeline(Jobs.JobStore store, TextureProcessor processor, IEtaEstimator eta, PipelineOptions options, ILogger&lt;ForgePipeline&gt;? logger = null)</c>.
/// </summary>
public interface IForgePipeline
{
    /// <summary>Gets the current state.</summary>
    PipelineState State { get; }

    /// <summary>
    /// Runs until every queued job is done or failed, or until <paramref name="stop"/> is cancelled. On start it recovers from a
    /// crash: interrupted jobs return to Queued and the work folder is wiped. On stop, items in flight are abandoned and returned to
    /// Queued; nothing partial reaches the output folder. Completes normally on stop (it does not throw <see cref="OperationCanceledException"/>).
    /// </summary>
    /// <param name="progress">Throttled progress sink.</param>
    /// <param name="stopToken">Requests a stop.</param>
    /// <returns>A summary of the run.</returns>
    Task<PipelineRunSummary> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken stopToken);

    /// <summary>Pauses: stages wait at their next safe point. Has no effect unless running.</summary>
    void Pause();

    /// <summary>Resumes after <see cref="Pause"/> (the UI's Resume button). Named to avoid the reserved word <c>Resume</c> (CA1716).</summary>
    void Unpause();
}

/// <summary>
/// Result of <see cref="IForgePipeline.RunAsync"/>.
/// </summary>
/// <param name="Done">Jobs finished.</param>
/// <param name="Failed">Jobs marked Failed.</param>
/// <param name="Requeued">Jobs returned to Queued because the run was stopped.</param>
/// <param name="Stopped">Whether the run ended because of a stop request.</param>
/// <param name="Elapsed">Run time excluding pauses.</param>
public sealed record PipelineRunSummary(int Done, int Failed, int Requeued, bool Stopped, TimeSpan Elapsed);

/// <summary>
/// Estimates remaining time from seconds per source megapixel per model, as an exponential moving average (IMPLEMENTATION_GUIDE §11).
/// Implementations are thread-safe.
/// </summary>
public interface IEtaEstimator
{
    /// <summary>Seeds the rate for a model, for example from the GPU profile, before any job has finished.</summary>
    /// <param name="model">Model name.</param>
    /// <param name="secondsPerMegapixel">Seconds per source megapixel.</param>
    void Seed(string model, double secondsPerMegapixel);

    /// <summary>Records a finished job.</summary>
    /// <param name="model">Model name (or a fixed key such as <c>lanczos</c> when no model ran).</param>
    /// <param name="sourceMegapixels">Source width × height / 1,000,000.</param>
    /// <param name="elapsed">Total processing time of the job.</param>
    void Record(string model, double sourceMegapixels, TimeSpan elapsed);

    /// <summary>Estimates the time to process <paramref name="remaining"/>.</summary>
    /// <param name="remaining">Model and source megapixels of each remaining job.</param>
    /// <param name="parallelism">Effective speed-up from overlapping stages (1 = serial).</param>
    /// <returns>The estimate, or <see langword="null"/> when some model has no rate yet.</returns>
    TimeSpan? Estimate(IEnumerable<(string Model, double SourceMegapixels)> remaining, double parallelism = 1.0);
}
