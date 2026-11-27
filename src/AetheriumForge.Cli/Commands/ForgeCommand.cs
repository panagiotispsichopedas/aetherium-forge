using System.CommandLine;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Pipeline;
using AetheriumForge.Cli.Rendering;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
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
        session.AttachProjectLog(project);

        var store = await JobStore.OpenAsync(project.DatabasePath, ct).ConfigureAwait(false);
        await using (store.ConfigureAwait(false))
        {
            var gamePath = await store.GetProjectValueAsync(ProjectKeys.GamePath, ct).ConfigureAwait(false)
                ?? throw new CliException($"The project {project.Root} has no game path. Run 'aetherium-forge scan --project \"{project.Root}\"' again.");
            var dataFolder = Path.Combine(gamePath, GameProfileLoader.DataFolderName);
            if (!Directory.Exists(dataFolder))
            {
                throw new CliException($"The game's Data folder {dataFolder} does not exist. Was the game moved? Run 'scan --game <dir>' again.");
            }

            ProjectLocator.EnsureOutsideGame(project, gamePath);
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

            output.WriteLine($"Project:  {project.Root}");
            output.WriteLine($"Output:   {project.OutputRoot}");
            output.WriteLine($"Models:   {string.Join(", ", needed)} ({modelsFolder})");
            output.WriteLine($"Device:   DirectML adapter {run.Device}, tile {run.TileSize} px; texconv {texconv}");
            output.WriteLine($"Jobs:     {(run.Limit is { } limit ? $"up to {limit:N0} of the queue" : "the whole queue")}{(run.Sequential ? " (sequential)" : string.Empty)}");
            output.WriteLine($"Log:      {session.Logging.LogFile}");
            output.WriteLine();

            var context = new ForgeContext(store, project, dataFolder, rules, models, texconv, run);
            using var host = CompositionRoot.Build(session.Logging, context);
            var pipeline = host.Services.GetRequiredService<IForgePipeline>();
            log.Information("Forging with {Pipeline} from {Project}: models {Models}, device {Device}, tile {Tile}, limit {Limit}",
                pipeline.GetType().Name, project.Root, needed, run.Device, run.TileSize, run.Limit);

            var interactive = !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out);
            var view = new ForgeConsoleView(output, interactive, readKeys: interactive && !Console.IsInputRedirected);
            PipelineRunSummary summary;
            using (session.Logging.QuietConsole())
            {
                var running = pipeline.RunAsync(view, session.Cancellation.Token);
                await view.RunAsync(pipeline, running, session.Cancellation).ConfigureAwait(false);
                summary = await running.ConfigureAwait(false);
            }

            output.WriteLine();
            output.WriteLine($"{(summary.Stopped ? "Stopped" : "Finished")} in {ProgressFormatter.FormatElapsed(summary.Elapsed)}: {summary.Done:N0} done, {summary.Failed:N0} failed, {summary.Requeued:N0} returned to the queue.");
            if (summary.Failed > 0)
            {
                output.WriteLine("Failed jobs keep their error; see 'aetherium-forge status' and the log.");
            }

            output.WriteLine($"Textures: {Path.Combine(project.OutputRoot, "textures")}");

            return summary.Stopped ? ExitCodes.Stopped : ExitCodes.Ok;
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
