using System.Globalization;
using System.Text;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Cli.Rendering;

/// <summary>
/// Formats <see cref="PipelineProgress"/> snapshots for the console. Pure functions: no console access, invariant culture.
/// </summary>
internal static class ProgressFormatter
{
    /// <summary>The number of lines <see cref="FormatLive"/> always returns (so the view can redraw in place).</summary>
    public const int LiveLineCount = 9;

    /// <summary>The narrowest layout supported; narrower consoles are formatted at this width.</summary>
    public const int MinimumWidth = 40;

    private const string KeyHint = "P pause · R resume · S stop · Ctrl+C stop (twice: force)";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Formats the live view: a header, one compact line per stage, an overall bar, throughput and ETA, the last error and the key hint.
    /// </summary>
    /// <param name="progress">The snapshot.</param>
    /// <param name="width">Console width in characters; each line is at most <c>width - 1</c> long.</param>
    /// <returns>Exactly <see cref="LiveLineCount"/> lines.</returns>
    public static IReadOnlyList<string> FormatLive(PipelineProgress progress, int width)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var max = Math.Max(MinimumWidth, width) - 1;
        var lines = new List<string>(LiveLineCount)
        {
            Fit($"Aetherium Forge · {StateLabel(progress.State)} · {progress.Done:N0} done · {progress.Failed:N0} failed · {progress.Remaining:N0} remaining", max),
        };

        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            var activity = progress.Stages.FirstOrDefault(s => s.Stage == stage);
            lines.Add(FormatStage(activity with { Stage = stage }, max));
        }

        var finished = progress.Done + progress.Failed;
        var fraction = progress.Total > 0 ? (double)finished / progress.Total : 0;
        var counter = string.Create(Invariant, $" {fraction * 100,5:0.0}%  {finished:N0}/{progress.Total:N0}");
        var barWidth = Math.Max(10, max - counter.Length - 2);
        lines.Add(Fit($"[{Bar(fraction, barWidth)}]{counter}", max));

        lines.Add(Fit(string.Create(Invariant, $"{progress.SourceMegapixelsPerSecond:0.00} MP/s · elapsed {FormatElapsed(progress.Elapsed)} · ETA {FormatEta(progress.Eta)}"), max));
        lines.Add(Fit(progress.LastError is null ? string.Empty : $"last error: {progress.LastError}", max));
        lines.Add(Fit(KeyHint, max));

        return lines;
    }

    /// <summary>
    /// Formats one self-contained line for redirected output (logs, CI).
    /// </summary>
    /// <param name="progress">The snapshot.</param>
    /// <returns>The line.</returns>
    public static string FormatPlain(PipelineProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var builder = new StringBuilder();
        builder.Append(Invariant, $"[{StateLabel(progress.State)}] {progress.Done + progress.Failed}/{progress.Total} ");
        builder.Append(Invariant, $"(done {progress.Done}, failed {progress.Failed}, remaining {progress.Remaining}) ");
        builder.Append(Invariant, $"{progress.SourceMegapixelsPerSecond:0.00} MP/s, elapsed {FormatElapsed(progress.Elapsed)}, ETA {FormatEta(progress.Eta)}");

        foreach (var stage in progress.Stages.Where(s => s.CurrentPath is not null))
        {
            builder.Append(Invariant, $" | {stage.Stage}: {stage.CurrentPath}");
            if (stage.TilesTotal > 0)
            {
                builder.Append(Invariant, $" ({stage.TilesDone}/{stage.TilesTotal} tiles)");
            }
        }

        return builder.ToString();
    }

    /// <summary>Formats a progress bar of <paramref name="width"/> cells.</summary>
    /// <param name="fraction">0 to 1 (clamped).</param>
    /// <param name="width">Cells.</param>
    /// <returns>Filled and empty cells.</returns>
    public static string Bar(double fraction, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        var filled = (int)Math.Round(Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>Formats elapsed time as <c>mm:ss</c> or <c>h:mm:ss</c>.</summary>
    /// <param name="elapsed">The duration.</param>
    /// <returns>The text.</returns>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? string.Create(Invariant, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
            : string.Create(Invariant, $"{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    /// <summary>Formats an ETA compactly (<c>3h 12m</c>, <c>4m 05s</c>, <c>12s</c>), or <c>--</c> when unknown.</summary>
    /// <param name="eta">The estimate.</param>
    /// <returns>The text.</returns>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } value)
        {
            return "--";
        }

        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalDays >= 1 ? string.Create(Invariant, $"{(int)value.TotalDays}d {value.Hours}h")
            : value.TotalHours >= 1 ? string.Create(Invariant, $"{(int)value.TotalHours}h {value.Minutes:00}m")
            : value.TotalMinutes >= 1 ? string.Create(Invariant, $"{value.Minutes}m {value.Seconds:00}s")
            : string.Create(Invariant, $"{value.Seconds}s");
    }

    /// <summary>Shortens <paramref name="text"/> to <paramref name="max"/> characters, keeping the end (the file name) of long paths.</summary>
    /// <param name="text">The text.</param>
    /// <param name="max">Maximum length.</param>
    /// <returns>The text, or <c>…</c> plus its tail.</returns>
    public static string TruncateStart(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= max ? text : max <= 1 ? "…" : "…" + text[^(max - 1)..];
    }

    private static string FormatStage(StageActivity activity, int max)
    {
        var name = activity.Stage.ToString().PadRight(12);
        if (activity.CurrentPath is null && activity.InFlight == 0)
        {
            var idle = activity.Queued > 0 ? string.Create(Invariant, $"waiting · {activity.Queued} queued") : "idle";
            return Fit($"  {name}{idle}", max);
        }

        var suffix = activity.TilesTotal > 0
            ? string.Create(Invariant, $"  tiles {activity.TilesDone}/{activity.TilesTotal}")
            : string.Empty;
        if (activity.InFlight > 1 || activity.Queued > 0)
        {
            suffix += string.Create(Invariant, $"  ({activity.InFlight} in flight, {activity.Queued} queued)");
        }

        var room = Math.Max(8, max - 2 - name.Length - suffix.Length);
        return Fit($"  {name}{TruncateStart(activity.CurrentPath ?? "…", room)}{suffix}", max);
    }

    private static string StateLabel(PipelineState state) => state switch
    {
        PipelineState.Running => "Running",
        PipelineState.Paused => "PAUSED",
        PipelineState.Stopping => "Stopping",
        _ => "Idle",
    };

    private static string Fit(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}
