using System.CommandLine;
using System.Globalization;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Rendering;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.DependencyInjection;

namespace AetheriumForge.Cli.Commands;

/// <summary>
/// <c>scan</c>: discovers the game, loads the profile, scans the gap set and imports it into the project's <c>forge.db</c>.
/// </summary>
internal sealed class ScanCommand
{
    private readonly GlobalOptions _globals;

    /// <summary>Initializes a new instance of the <see cref="ScanCommand"/> class.</summary>
    /// <param name="globals">The global options.</param>
    public ScanCommand(GlobalOptions globals)
    {
        ArgumentNullException.ThrowIfNull(globals);

        _globals = globals;
        Command = new Command("scan", "Find the textures the game still loads from its original archives and queue them in the project.")
        {
            Game,
            Project,
            Rules,
        };
        Command.SetAction((parseResult, ct) => CliRunner.RunAsync(parseResult, _globals, session => RunAsync(parseResult, session), ct));
    }

    /// <summary>Gets the command.</summary>
    public Command Command { get; }

    /// <summary>Gets <c>--game</c>.</summary>
    public Option<string?> Game { get; } = new("--game") { Description = "Game folder (the one with SkyrimSE.exe). Default: discovered from Steam, GOG, Epic or the registry.", HelpName = "dir" };

    /// <summary>Gets <c>--project</c>.</summary>
    public Option<string?> Project { get; } = new("--project") { Description = "Project folder. Default: <best drive>\\AetheriumForge\\Projects\\<game folder name>.", HelpName = "dir" };

    /// <summary>Gets <c>--rules</c>.</summary>
    public Option<string?> Rules { get; } = new("--rules") { Description = "Rules JSON. Default: the project's stored rules, else the built-in defaults.", HelpName = "file.json" };

    private async Task<int> RunAsync(ParseResult parseResult, CliSession session)
    {
        var ct = session.Cancellation.Token;
        var output = session.Out;
        using var host = CompositionRoot.Build(session.Logging);
        var services = host.Services;

        // 1. Game
        var game = await FindGameAsync(services.GetRequiredService<GameLocator>(), parseResult.GetValue(Game), output, ct).ConfigureAwait(false);
        output.WriteLine($"Game:     {game.GameFolder} ({game.Store}{(game.ExeVersion is { } v ? $", {v}" : string.Empty)})");

        // 2. Project: an explicit or existing one is known now, so its log can start before the scan.
        var drives = DriveAdvisor.GetFixedDrives();
        var known = !string.IsNullOrWhiteSpace(parseResult.GetValue(Project)) || ProjectLocator.FindExisting(game.GameFolder, drives, File.Exists) is not null;
        (ProjectLayout Layout, string Reason)? project = known
            ? ProjectLocator.ResolveForScan(parseResult.GetValue(Project), game.GameFolder, drives, 0, File.Exists)
            : null;
        if (project is { } early)
        {
            session.AttachProjectLog(early.Layout);
        }

        // 3. Profile
        var paths = services.GetRequiredService<ProfileLocator>().Locate(game.Store);
        output.WriteLine($"Profile:  {paths.IniFolder}");
        output.WriteLine($"Plugins:  {paths.PluginsTxtPath}{(File.Exists(paths.PluginsTxtPath) ? string.Empty : " (not found: base game and Creation Club only)")}");
        var profile = await GameProfileLoader.LoadAsync(game.GameFolder, paths.IniFolder, paths.PluginsTxtPath, null, ct).ConfigureAwait(false);
        output.WriteLine($"Load order: {profile.LoadOrder.Count} plugins, {profile.IniArchives.Count} INI archives, {profile.PluginArchives.Count} plugin archives");

        // 4. Rules
        var rules = await LoadRulesAsync(parseResult.GetValue(Rules), project?.Layout, output, ct).ConfigureAwait(false);

        // 5. Scan
        var interactive = !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out);
        var scanProgress = new ScanProgressPrinter(output, interactive);
        var result = await services.GetRequiredService<TextureScanner>().ScanAsync(profile, rules, scanProgress, ct).ConfigureAwait(false);
        scanProgress.Complete();

        var queued = result.Queued.ToList();
        var outputBytes = DriveAdvisor.EstimateOutputBytes(queued.Select(t => Math.Max(t.Decision.TargetWidth, t.Decision.TargetHeight)));
        var tempBytes = DriveAdvisor.EstimateTempBytes(2, queued.Count == 0 ? 0 : queued.Max(t => Math.Max(t.Decision.TargetWidth, t.Decision.TargetHeight)));

        // 6. Project folder (now with the size estimate) and import
        var (layout, reason) = project ?? ProjectLocator.ResolveForScan(null, game.GameFolder, drives, outputBytes + tempBytes, File.Exists);
        session.AttachProjectLog(layout);
        output.WriteLine($"Project:  {layout.Root} ({reason})");
        Directory.CreateDirectory(layout.Root);

        var store = await JobStore.OpenAsync(layout.DatabasePath, ct).ConfigureAwait(false);
        await using (store.ConfigureAwait(false))
        {
            var (upserted, nowProvided) = await ScanImporter.ImportAsync(store, result, ct).ConfigureAwait(false);
            await store.SetProjectValueAsync(ProjectKeys.GamePath, game.GameFolder, ct).ConfigureAwait(false);
            await store.SetProjectValueAsync(ProjectKeys.GameStore, game.Store.ToString(), ct).ConfigureAwait(false);
            await store.SetProjectValueAsync(ProjectKeys.RulesJson, rules.ToJson(), ct).ConfigureAwait(false);
            await store.SetProjectValueAsync(ProjectKeys.ScannedAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            var counts = await store.CountByStateAsync(ct).ConfigureAwait(false);

            output.WriteLine();
            foreach (var line in ScanSummaryFormatter.Format(result, outputBytes))
            {
                output.WriteLine(line);
            }

            output.WriteLine();
            output.WriteLine($"Imported {upserted:N0} rows into {layout.DatabasePath}; {nowProvided:N0} job(s) now provided by a mod.");
            output.WriteLine($"Project queue: {counts[JobState.Queued]:N0} queued, {counts[JobState.Done]:N0} done, {counts[JobState.Failed]:N0} failed, {counts[JobState.Skipped]:N0} skipped.");
            output.WriteLine($"Next: aetherium-forge forge --project \"{layout.Root}\" --limit 3");
        }

        return ExitCodes.Ok;
    }

    private static async Task<GameInstall> FindGameAsync(GameLocator locator, string? explicitGame, TextWriter output, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitGame))
        {
            return locator.Validate(Path.GetFullPath(explicitGame))
                ?? throw new CliException($"{explicitGame} is not a Skyrim Special Edition folder (no {GameLocator.ExecutableName} and Data folder).");
        }

        var installs = await locator.FindAllAsync(ct).ConfigureAwait(false);
        if (installs.Count == 0)
        {
            throw new CliException("Skyrim Special Edition was not found (Steam, GOG, Epic, registry). Pass --game <dir>.");
        }

        if (installs.Count > 1)
        {
            output.WriteLine($"Found {installs.Count} installations; using the first. Others: {string.Join(", ", installs.Skip(1).Select(i => i.GameFolder))}");
        }

        return installs[0];
    }

    private static async Task<ForgeRules> LoadRulesAsync(string? rulesFile, ProjectLayout? existing, TextWriter output, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(rulesFile))
        {
            if (!File.Exists(rulesFile))
            {
                throw new CliException($"Rules file {rulesFile} does not exist.");
            }

            output.WriteLine($"Rules:    {Path.GetFullPath(rulesFile)}");
            return await ForgeRules.LoadAsync(rulesFile, ct).ConfigureAwait(false);
        }

        if (existing is not null && File.Exists(existing.DatabasePath))
        {
            var store = await JobStore.OpenAsync(existing.DatabasePath, ct).ConfigureAwait(false);
            await using (store.ConfigureAwait(false))
            {
                if (await store.GetProjectValueAsync(ProjectKeys.RulesJson, ct).ConfigureAwait(false) is { } json)
                {
                    output.WriteLine("Rules:    the project's stored rules");
                    return ForgeRules.FromJson(json);
                }
            }
        }

        output.WriteLine("Rules:    built-in defaults");
        return ForgeRules.Default;
    }

    /// <summary>Prints scan progress on one updating line (or one line per phase when redirected).</summary>
    private sealed class ScanProgressPrinter(TextWriter output, bool interactive) : IProgress<ScanProgress>
    {
        private readonly Lock _gate = new();
        private ScanPhase? _phase;
        private long _lastDraw;
        private bool _dirty;

        public void Report(ScanProgress value)
        {
            lock (_gate)
            {
                if (!interactive)
                {
                    if (_phase != value.Phase)
                    {
                        _phase = value.Phase;
                        output.WriteLine($"Scanning: {value.Phase}...");
                    }

                    return;
                }

                var now = Environment.TickCount64;
                if (_phase == value.Phase && now - _lastDraw < 100 && value.Done < value.Total)
                {
                    return;
                }

                _phase = value.Phase;
                _lastDraw = now;
                _dirty = true;
                var fraction = value.Total > 0 ? (double)value.Done / value.Total : 0;
                output.Write($"\rScanning: {value.Phase,-12} [{ProgressFormatter.Bar(fraction, 30)}] {value.Done:N0}/{value.Total:N0}   ");
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_dirty)
                {
                    output.WriteLine();
                }
            }
        }
    }
}
