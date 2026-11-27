using System.CommandLine;
using System.Globalization;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Core.Jobs;

namespace AetheriumForge.Cli.Commands;

/// <summary>
/// <c>status</c>: job counts by state, the most common errors, the last runs and the output folder.
/// </summary>
internal sealed class StatusCommand
{
    private const int RunsShown = 5;
    private const int ErrorsShown = 5;

    private readonly GlobalOptions _globals;

    /// <summary>Initializes a new instance of the <see cref="StatusCommand"/> class.</summary>
    /// <param name="globals">The global options.</param>
    public StatusCommand(GlobalOptions globals)
    {
        ArgumentNullException.ThrowIfNull(globals);

        _globals = globals;
        Command = new Command("status", "Show the project's queue, recent runs and output folder.") { Project };
        Command.SetAction((parseResult, ct) => CliRunner.RunAsync(parseResult, _globals, session => RunAsync(parseResult, session), ct));
    }

    /// <summary>Gets the command.</summary>
    public Command Command { get; }

    /// <summary>Gets <c>--project</c>.</summary>
    public Option<string?> Project { get; } = new("--project") { Description = "Project folder. Default: the project of the discovered game.", HelpName = "dir" };

    private async Task<int> RunAsync(ParseResult parseResult, CliSession session)
    {
        var ct = session.Cancellation.Token;
        var output = session.Out;
        var project = await ForgeCommand.ResolveExistingProjectAsync(parseResult.GetValue(Project), session, ct).ConfigureAwait(false);
        session.AttachProjectLog(project);

        var store = await JobStore.OpenAsync(project.DatabasePath, ct).ConfigureAwait(false);
        await using (store.ConfigureAwait(false))
        {
            var counts = await store.CountByStateAsync(ct).ConfigureAwait(false);
            var total = counts.Values.Sum();
            output.WriteLine($"Project:  {project.Root}");
            output.WriteLine($"Game:     {await store.GetProjectValueAsync(ProjectKeys.GamePath, ct).ConfigureAwait(false) ?? "(unknown)"}");
            output.WriteLine($"Scanned:  {FormatTimestamp(await store.GetProjectValueAsync(ProjectKeys.ScannedAt, ct).ConfigureAwait(false))}");
            output.WriteLine();
            foreach (var state in Enum.GetValues<JobState>())
            {
                output.WriteLine($"  {state,-8} {counts[state],8:N0}");
            }

            output.WriteLine($"  {"Total",-8} {total,8:N0}");

            if (counts[JobState.Failed] > 0)
            {
                var errors = new Dictionary<string, int>(StringComparer.Ordinal);
                await foreach (var job in store.GetJobsAsync(JobState.Failed, ct).ConfigureAwait(false))
                {
                    var key = job.Error ?? "(no message)";
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                }

                output.WriteLine();
                output.WriteLine("Most common errors:");
                foreach (var (error, count) in errors.OrderByDescending(e => e.Value).Take(ErrorsShown))
                {
                    output.WriteLine($"  {count,6:N0} × {error}");
                }
            }

            output.WriteLine();
            var runs = await RunHistoryReader.ReadLatestAsync(project.DatabasePath, RunsShown, ct).ConfigureAwait(false);
            if (runs.Count == 0)
            {
                output.WriteLine("Runs:     none yet");
            }
            else
            {
                output.WriteLine("Last runs:");
                foreach (var run in runs)
                {
                    var duration = run is { Started: { } started, Ended: { } ended } ? Rendering.ProgressFormatter.FormatElapsed(ended - started) : "unfinished";
                    output.WriteLine($"  #{run.Id,-4} {FormatTimestamp(run.Started),-19}  {duration,9}  {run.Done,6:N0} done  {run.Failed,4:N0} failed");
                }
            }

            output.WriteLine();
            var textures = Path.Combine(project.OutputRoot, "textures");
            if (Directory.Exists(textures))
            {
                var files = new DirectoryInfo(textures).EnumerateFiles("*.dds", SearchOption.AllDirectories).ToList();
                output.WriteLine($"Output:   {textures} ({files.Count:N0} files, {ProjectLocator.FormatBytes(files.Sum(f => f.Length))})");
            }
            else
            {
                output.WriteLine($"Output:   {textures} (empty)");
            }
        }

        return ExitCodes.Ok;
    }

    private static string FormatTimestamp(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? FormatTimestamp(value) : "never";

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?";
}
