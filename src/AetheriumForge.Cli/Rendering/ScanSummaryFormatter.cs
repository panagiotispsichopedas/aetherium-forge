using System.Globalization;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Cli.Rendering;

/// <summary>
/// Formats the scan summary: gaps, queued and skipped counts by reason, a group × kind table and the estimated output size.
/// </summary>
internal static class ScanSummaryFormatter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Formats the summary.</summary>
    /// <param name="result">The scan result.</param>
    /// <param name="outputBytes">Estimated BC7 output size of the queued textures.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Format(ScanResult result, long outputBytes)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>();
        var queued = result.Queued.ToList();
        var skipped = result.Skipped.ToList();

        lines.Add(string.Create(Invariant, $"Gaps:     {result.Textures.Count,8:N0} textures loaded from the original archives"));
        lines.Add(string.Create(Invariant, $"          ({result.LooseTextureCount:N0} loose textures and {result.ModArchiveCount:N0} mod archives override, {result.BaseArchiveCount:N0} base archives scanned)"));
        if (result.MissingArchives.Count > 0)
        {
            lines.Add($"          missing archives: {string.Join(", ", result.MissingArchives)}");
        }

        lines.Add(string.Create(Invariant, $"Queued:   {queued.Count,8:N0}   estimated output {ProjectLocator.FormatBytes(outputBytes)}"));
        lines.Add(string.Create(Invariant, $"Skipped:  {skipped.Count,8:N0}"));
        foreach (var reason in skipped.GroupBy(t => t.Decision.Skip).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            lines.Add(string.Create(Invariant, $"  {reason.Key,-18}{reason.Count(),8:N0}"));
        }

        if (queued.Count == 0)
        {
            return lines;
        }

        var rows = queued
            .GroupBy(t => (Group: t.Decision.Group ?? "(none)", t.Decision.Kind, Model: t.Decision.Model ?? "-"))
            .Select(g => (g.Key.Group, Kind: g.Key.Kind.ToString(), g.Key.Model, Count: g.Count(),
                Bytes: DriveAdvisor.EstimateOutputBytes(g.Select(t => Math.Max(t.Decision.TargetWidth, t.Decision.TargetHeight)))))
            .OrderBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Count)
            .ToList();

        var groupWidth = Math.Max(5, rows.Max(r => r.Group.Length));
        var kindWidth = Math.Max(4, rows.Max(r => r.Kind.Length));
        var modelWidth = Math.Max(5, rows.Max(r => r.Model.Length));

        lines.Add(string.Empty);
        lines.Add($"  {"Group".PadRight(groupWidth)}  {"Kind".PadRight(kindWidth)}  {"Model".PadRight(modelWidth)}  {"Count",8}  {"Output",10}");
        lines.Add($"  {new string('-', groupWidth)}  {new string('-', kindWidth)}  {new string('-', modelWidth)}  {new string('-', 8)}  {new string('-', 10)}");
        foreach (var row in rows)
        {
            lines.Add(string.Create(Invariant, $"  {row.Group.PadRight(groupWidth)}  {row.Kind.PadRight(kindWidth)}  {row.Model.PadRight(modelWidth)}  {row.Count,8:N0}  {ProjectLocator.FormatBytes(row.Bytes),10}"));
        }

        lines.Add(string.Create(Invariant, $"  {"Total".PadRight(groupWidth + kindWidth + modelWidth + 4)}  {queued.Count,8:N0}  {ProjectLocator.FormatBytes(outputBytes),10}"));

        return lines;
    }
}
