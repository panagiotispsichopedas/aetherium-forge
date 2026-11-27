using System.CommandLine;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Pipeline;
using AetheriumForge.Cli.Rendering;
using AetheriumForge.Core.Concurrency;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AetheriumForge.Cli.Commands;

/// <summary>
/// <c>forge</c>: runs the queued jobs through an <see cref="IForgePipeline"/> with a live console view and P/R/S keys.
/// </summary>
internal sealed class ForgeCommand
{
    /// <summary>The default upscaler tile size.</summary>
    public const int DefaultTileSize = 384;

    /// <summary>The smallest accepted tile size (the tiler's minimum).</summary>
    public const int MinTileSize = 64;

    /// <summary>The largest accepted tile size.</summary>
    public const int MaxTileSize = 2048;

    /// <summary>How long <c>forge</c> waits for the GPU work lock before failing.</summary>
    internal static readonly TimeSpan GpuLockTimeout = TimeSpan.FromSeconds(5);

    private const string OwnerDescription = "aetherium-forge forge";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly GlobalOptions _globals;

    /// <summary>Initializes a new instance of the <see cref="ForgeCommand"/> class.</summary>
    /// <param name="globals">The global options.</param>
    public ForgeCommand(GlobalOptions globals)
    {
        ArgumentNullException.ThrowIfNull(globals);

        _globals = globals;
        AddRangeValidator(Device, 0, int.MaxValue, "--device must be 0 or greater.");
        AddRangeValidator(Tile, MinTileSize, MaxTileSize, $"--tile must be between {MinTileSize} and {MaxTileSize}.");
        AddRangeValidator(Limit, 1, int.MaxValue, "--limit must be 1 or greater.");

        Command = new Command("forge", "Upscale the queued textures. Keys: P pause, R resume, S stop; Ctrl+C stops gracefully, twice forces exit.")
        {
            Project,
            Models,
            Model,
            Device,
            Tile,
            Limit,
            Texconv,
            Sequential,
        };
        Command.SetAction((parseResult, ct) => CliRunner.RunAsync(parseResult, _globals, session => RunAsync(parseResult, session), ct));
    }

    /// <summary>Gets the command.</summary>
    public Command Command { get; }

    /// <summary>Gets <c>--project</c>.</summary>
    public Option<string?> Project { get; } = new("--project") { Description = "Project folder. Default: the project of the discovered game.", HelpName = "dir" };

    /// <summary>Gets <c>--models</c>.</summary>
    public Option<string?> Models { get; } = new("--models") { Description = $"Models folder. Default: {ModelCatalog.DefaultModelsFolder}.", HelpName = "dir" };

    /// <summary>Gets <c>--model</c>.</summary>
    public Option<string?> Model { get; } = new("--model") { Description = "Use this model for every job instead of the job's own.", HelpName = "name" };

    /// <summary>Gets <c>--device</c>.</summary>
    public Option<int> Device { get; } = new("--device") { Description = "DirectML adapter index.", DefaultValueFactory = _ => 0, HelpName = "n" };

    /// <summary>Gets <c>--tile</c>.</summary>
    public Option<int> Tile { get; } = new("--tile") { Description = "Upscaler tile size in pixels.", DefaultValueFactory = _ => DefaultTileSize, HelpName = "px" };

    /// <summary>Gets <c>--limit</c>.</summary>
    public Option<int?> Limit { get; } = new("--limit") { Description = "Process at most this many jobs (handy for a quick test).", HelpName = "n" };

    /// <summary>Gets <c>--texconv</c>.</summary>
    public Option<string?> Texconv { get; } = new("--texconv") { Description = "texconv.exe. Default: <exe dir>\\tools\\texconv.exe, then <repo>\\tools\\texconv.exe.", HelpName = "path" };

    /// <summary>Gets <c>--sequential</c>.</summary>
    public Option<bool> Sequential { get; } = new("--sequential") { Description = "Process one texture at a time (debugging fallback)." };

    private static void AddRangeValidator(Option option, int min, int max, string message) =>
        option.Validators.Add(result =>
        {
            // Only explicit values are checked; a token that is not a number is already reported by the parser.
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && (value < min || value > max))
            {
                result.AddError(message);
            }
        });

    private async Task<int> RunAsync(ParseResult parseResult, CliSession session)
    {
        var ct = session.Cancellation.Token;
        var output = session.Out;
        var log = session.Logging.Logger.ForContext<ForgeCommand>();

        var project = await ResolveProjectAsync(parseResult.GetValue(Project), session, ct).ConfigureAwait(false);

        // Containment first: nothing (log, database, work files) is written until the project is known to be outside the game.
        var gamePath = await ProjectDatabaseReader.ReadProjectValueAsync(project.DatabasePath, ProjectKeys.GamePath, ct).ConfigureAwait(false)
            ?? throw new CliException($"The project {project.Root} has no game path. Run 'aetherium-forge scan --project \"{project.Root}\"' again.");
        ProjectLocator.EnsureOutsideGame(project, gamePath);

        // Lock the project before the log, the store or work files are touched: one process per project.
        using var projectLock = AcquireProjectLock(project.Root);
        session.AttachLog(project, gamePath);

        var store = await JobStore.OpenAsync(project.DatabasePath, ct).ConfigureAwait(false);
        await using (store.ConfigureAwait(false))
        {
            var dataFolder = Path.Combine(gamePath, GameProfileLoader.DataFolderName);
            if (!Directory.Exists(dataFolder))
            {
                throw new CliException($"The game's Data folder {dataFolder} does not exist. Was the game moved? Run 'scan --game <dir>' again.");
            }

            var rulesJson = await store.GetProjectValueAsync(ProjectKeys.RulesJson, ct).ConfigureAwait(false);
            var rules = rulesJson is null ? ForgeRules.Default : ForgeRules.FromJson(rulesJson);

            var run = new ForgeRunSettings(
                parseResult.GetValue(Model),
                parseResult.GetValue(Device),
                parseResult.GetValue(Tile),
                parseResult.GetValue(Limit),
                parseResult.GetValue(Sequential));
            var mapper = new JobMapper(rules, run.ModelOverride);

            var jobs = await PeekQueuedAsync(store, run.Limit, ct).ConfigureAwait(false);
            if (jobs.Count == 0)
            {
                var counts = await store.CountByStateAsync(ct).ConfigureAwait(false);
                output.WriteLine($"Nothing queued in {project.Root} ({counts[JobState.Done]:N0} done, {counts[JobState.Failed]:N0} failed, {counts[JobState.Skipped]:N0} skipped).");
                return ExitCodes.Ok;
            }

            var modelsFolder = Path.GetFullPath(parseResult.GetValue(Models) ?? ModelCatalog.DefaultModelsFolder);
            using var loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(session.Logging.Logger);
            var models = ModelCatalog.Discover(modelsFolder, loggerFactory.CreateLogger(nameof(ModelCatalog)));
            var needed = RequiredModels(jobs, mapper);
            EnsureModelsInstalled(needed, models, modelsFolder);

            var texconv = ToolLocator.FindTexconv(parseResult.GetValue(Texconv), AppContext.BaseDirectory, File.Exists);
            log.Information("Using texconv {TexconvPath}", texconv);

            output.WriteLine($"Project:  {project.Root}");
            output.WriteLine($"Output:   {project.OutputRoot}");
            output.WriteLine($"Models:   {string.Join(", ", needed)} ({modelsFolder})");
            output.WriteLine($"Device:   DirectML adapter {run.Device}, tile {run.TileSize} px; texconv {texconv}");
            output.WriteLine($"Jobs:     {(run.Limit is { } limit ? $"up to {limit:N0} of the queue" : "the whole queue")}{(run.Sequential ? " (sequential)" : string.Empty)}");
            output.WriteLine($"Log:      {session.Logging.LogFile}");
            output.WriteLine();

            // One GPU job per machine: wait briefly for a calibration or another run to finish, then fail naming the holder.
            using var gpuLock = await AcquireGpuLockAsync(loggerFactory, ct).ConfigureAwait(false);

            var context = new ForgeContext(store, project, dataFolder, rules, models, texconv, run, session.Cancellation.Processes);
            using var host = CompositionRoot.Build(session.Logging, context);
            var pipeline = host.Services.GetRequiredService<IForgePipeline>();
            log.Information("Forging with {Pipeline} from {Project}: models {Models}, device {Device}, tile {Tile}, limit {Limit}",
                pipeline.GetType().Name, project.Root, needed, run.Device, run.TileSize, run.Limit);

            var interactive = !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out);
            var view = new ForgeConsoleView(output, interactive, readKeys: !Console.IsInputRedirected);
            // Both pipelines apply --limit themselves (PipelineOptions.MaxJobs / SequentialPipeline), so jobs past the limit are never
            // started and nothing in flight is thrown away.
            ForgeRunOutcome outcome;
            using (session.Logging.QuietConsole())
            {
                outcome = await RunWithViewAsync(pipeline, view, session.Cancellation, log, limit: null).ConfigureAwait(false);
            }

            var summary = outcome.Summary;
            var userStopped = session.Cancellation.IsCancellationRequested;
            var verdict = userStopped ? "Stopped" : run.Limit is { } jobLimit ? $"Finished (limit of {jobLimit:N0})" : "Finished";
            output.WriteLine();
            output.WriteLine($"{verdict} in {ProgressFormatter.FormatElapsed(summary.Elapsed)}: {summary.Done:N0} done, {summary.Failed:N0} failed, {summary.Requeued:N0} returned to the queue.");
            if (summary.Failed > 0)
            {
                output.WriteLine("Failed jobs keep their error; see 'aetherium-forge status' and the log.");
            }

            output.WriteLine($"Textures: {Path.Combine(project.OutputRoot, "textures")}");

            return userStopped ? ExitCodes.Stopped : ExitCodes.Ok;
        }
    }

    /// <summary>
    /// Runs the pipeline under the live view. The pipeline task is always awaited before returning, so the host (GPU sessions, archive
    /// readers, the job store) is never disposed under a running pipeline. If the view fails, the run is stopped first and the view's
    /// error is rethrown after the pipeline has finished.
    /// </summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="view">The view (also the progress sink).</param>
    /// <param name="cancellation">The stop source.</param>
    /// <param name="log">Logger.</param>
    /// <param name="limit">Stop the run once this many jobs finished (done or failed), or <see langword="null"/> to run the whole queue.</param>
    /// <returns>The run summary, and whether the run ended because the limit was reached.</returns>
    internal static async Task<ForgeRunOutcome> RunWithViewAsync(IForgePipeline pipeline, ForgeConsoleView view, ConsoleCancellation cancellation, ILogger log, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(log);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        using var throttled = new ThrottledProgress<PipelineProgress>(view, ProgressInterval);
        var limitReached = 0;
        IProgress<PipelineProgress> sink = limit is not { } max
            ? throttled
            : new LimitProgress(throttled, max, () =>
            {
                if (Interlocked.Exchange(ref limitReached, 1) == 0)
                {
                    log.Information("Limit of {Limit} job(s) reached; stopping the run", max);
                    stop.Cancel();
                }
            });

        // The view stops drawing when this task completes, so the final snapshot is flushed to it first.
        var running = RunAndFlushAsync(pipeline.RunAsync(sink, stop.Token), throttled);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? viewFailure = null;
        try
        {
            await view.RunAsync(pipeline, running, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error(ex, "The live view failed; stopping the run");
            viewFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            cancellation.Cancel();
        }

        var summary = await running.ConfigureAwait(false);
        viewFailure?.Throw();
        return new ForgeRunOutcome(summary, Volatile.Read(ref limitReached) == 1 && !cancellation.IsCancellationRequested);
    }

    private static async Task<PipelineRunSummary> RunAndFlushAsync(Task<PipelineRunSummary> run, ThrottledProgress<PipelineProgress> throttled)
    {
        try
        {
            return await run.ConfigureAwait(false);
        }
        finally
        {
            throttled.Flush();
        }
    }

    /// <summary>Lists the models the jobs need (parallax/height maps never run a model).</summary>
    /// <param name="jobs">The jobs about to run.</param>
    /// <param name="mapper">Applies <c>--model</c>.</param>
    /// <returns>Distinct model names, sorted.</returns>
    internal static IReadOnlyList<string> RequiredModels(IEnumerable<JobRecord> jobs, JobMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(mapper);

        return [.. jobs
            .Where(j => !string.Equals(j.Kind, nameof(TextureKind.Parallax), StringComparison.OrdinalIgnoreCase))
            .Select(j => mapper.ModelFor(j))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Fails with instructions when a needed model is not installed.</summary>
    /// <param name="needed">Models the jobs need.</param>
    /// <param name="installed">Installed models.</param>
    /// <param name="modelsFolder">The models folder.</param>
    /// <exception cref="CliException">A model is missing.</exception>
    internal static void EnsureModelsInstalled(IReadOnlyList<string> needed, IReadOnlyList<ModelInfo> installed, string modelsFolder)
    {
        ArgumentNullException.ThrowIfNull(needed);
        ArgumentNullException.ThrowIfNull(installed);

        var missing = needed.Where(n => !installed.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var have = installed.Count == 0 ? "none" : string.Join(", ", installed.Select(m => m.Name));
        throw new CliException(
            $"Model(s) not installed in {modelsFolder}: {string.Join(", ", missing)} (installed: {have}).{Environment.NewLine}"
            + $"Get them with: {ModelsCommand.DownloadHint(modelsFolder)}{Environment.NewLine}"
            + "or pick an installed one with --model <name>.");
    }

    /// <summary>Takes the project lock, turning a held lock into a user-facing error.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The lock.</returns>
    /// <exception cref="CliException">The project is open in another process.</exception>
    internal static ProjectLock AcquireProjectLock(string projectRoot)
    {
        try
        {
            return ProjectLock.Acquire(projectRoot, OwnerDescription);
        }
        catch (ProjectLockedException ex)
        {
            throw new CliException(ex.Message, ex);
        }
    }

    /// <summary>Takes the GPU work lock for the run, waiting up to <see cref="GpuLockTimeout"/>.</summary>
    /// <param name="loggerFactory">Creates the lock's logger.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The held lock.</returns>
    /// <exception cref="CliException">The GPU is busy with another job.</exception>
    private static async Task<IDisposable> AcquireGpuLockAsync(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var gpu = new GpuWorkLock(OwnerDescription, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<GpuWorkLock>(loggerFactory));
        try
        {
            return await gpu.AcquireAsync("a forge run", GpuLockTimeout, ct).ConfigureAwait(false);
        }
        catch (GpuBusyException ex)
        {
            throw new CliException(ex.Message, ex);
        }
    }

    private static async Task<ProjectLayout> ResolveProjectAsync(string? explicitProject, CliSession session, CancellationToken ct)
    {
        IReadOnlyList<string> games = [];
        if (string.IsNullOrWhiteSpace(explicitProject))
        {
            using var host = CompositionRoot.Build(session.Logging);
            var installs = await host.Services.GetRequiredService<GameLocator>().FindAllAsync(ct).ConfigureAwait(false);
            games = [.. installs.Select(i => i.GameFolder)];
        }

        return ProjectLocator.ResolveExisting(explicitProject, games, DriveAdvisor.GetFixedDrives(), File.Exists);
    }

    private static async Task<List<JobRecord>> PeekQueuedAsync(JobStore store, int? limit, CancellationToken ct)
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in store.GetJobsAsync(JobState.Queued, ct).ConfigureAwait(false))
        {
            jobs.Add(job);
            if (limit is { } max && jobs.Count >= max)
            {
                break;
            }
        }

        return jobs;
    }

    /// <summary>Resolves the project for commands that need an existing one (shared with <c>status</c>).</summary>
    /// <param name="explicitProject">The <c>--project</c> value.</param>
    /// <param name="session">The session.</param>
    /// <param name="ct">Cancels discovery.</param>
    /// <returns>The project.</returns>
    internal static Task<ProjectLayout> ResolveExistingProjectAsync(string? explicitProject, CliSession session, CancellationToken ct) =>
        ResolveProjectAsync(explicitProject, session, ct);
}

/// <summary>
/// The result of a forge run under the live view.
/// </summary>
/// <param name="Summary">The pipeline's summary.</param>
/// <param name="LimitReached">The CLI stopped the run because <c>--limit</c> jobs had finished (not a user stop).</param>
internal sealed record ForgeRunOutcome(PipelineRunSummary Summary, bool LimitReached);

/// <summary>
/// Forwards progress and calls <c>onLimit</c> once <see cref="PipelineProgress.Done"/> + <see cref="PipelineProgress.Failed"/> reaches the limit.
/// </summary>
/// <param name="inner">The next sink.</param>
/// <param name="limit">Jobs to finish.</param>
/// <param name="onLimit">Called when the limit is reached (possibly more than once).</param>
internal sealed class LimitProgress(IProgress<PipelineProgress> inner, int limit, Action onLimit) : IProgress<PipelineProgress>
{
    /// <inheritdoc />
    public void Report(PipelineProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        inner.Report(value);
        if (value.Done + value.Failed >= limit)
        {
            onLimit();
        }
    }
}
