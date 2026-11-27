using System.Globalization;
using System.Numerics;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>
/// One line of a summary card: a label, a count and, for queued textures, the estimated output size.
/// </summary>
/// <param name="Label">What is counted.</param>
/// <param name="Count">How many textures.</param>
/// <param name="Bytes">Estimated BC7 output size, or <see langword="null"/>.</param>
public sealed record SummaryRow(string Label, int Count, long? Bytes)
{
    /// <summary>Gets the count with thousands separators.</summary>
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Gets the size text, or an empty string.</summary>
    public string BytesText => Bytes is { } bytes ? ProjectLocator.FormatBytes(bytes) : string.Empty;
}

/// <summary>
/// The Scan page's summary cards (IMPLEMENTATION_GUIDE §14 "Scan"): gaps, queued and skipped by reason, queued textures per group, kind and
/// resolution bucket, and the estimated output size. Built from a fresh scan, or from the job rows of an opened project.
/// </summary>
/// <param name="Gaps">Textures the game still loads from its original archives.</param>
/// <param name="Queued">Textures that will be upscaled (for an opened project: every job that is not skipped).</param>
/// <param name="Skipped">Textures the rules skip.</param>
/// <param name="EstimatedOutputBytes">Estimated BC7 output size of the queued textures.</param>
/// <param name="SkippedByReason">Skipped textures by reason, most first.</param>
/// <param name="ByGroup">Queued textures by rule group.</param>
/// <param name="ByKind">Queued textures by texture kind, most first.</param>
/// <param name="ByResolution">Queued textures by target resolution bucket, smallest first.</param>
/// <param name="Source">Where the numbers come from, for example <c>Scan of …</c>.</param>
public sealed record ScanSummary(
    int Gaps,
    int Queued,
    int Skipped,
    long EstimatedOutputBytes,
    IReadOnlyList<SummaryRow> SkippedByReason,
    IReadOnlyList<SummaryRow> ByGroup,
    IReadOnlyList<SummaryRow> ByKind,
    IReadOnlyList<SummaryRow> ByResolution,
    string Source)
{
    /// <summary>Gets the gaps with thousands separators.</summary>
    public string GapsText => Gaps.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Gets the queued count with thousands separators.</summary>
    public string QueuedText => Queued.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Gets the skipped count with thousands separators.</summary>
    public string SkippedText => Skipped.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Gets the estimated output size.</summary>
    public string EstimatedOutputText => ProjectLocator.FormatBytes(EstimatedOutputBytes);

    /// <summary>Builds the summary of a scan.</summary>
    /// <param name="result">The scan.</param>
    /// <param name="source">Where the numbers come from.</param>
    /// <returns>The summary.</returns>
    public static ScanSummary FromScan(ScanResult result, string source)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(source);

        var entries = result.Textures
            .Select(t => new Entry(
                t.Decision.IsSkipped ? t.Decision.Skip.ToString() : null,
                t.Decision.Group,
                t.Decision.Kind.ToString(),
                Math.Max(t.Decision.TargetWidth, t.Decision.TargetHeight)))
            .ToList();
        return Build(entries, source);
    }

    /// <summary>
    /// Builds the summary of an opened project from its job rows. Jobs skipped because a mod now provides them are no longer gaps and are
    /// left out; done, failed and running jobs count as queued.
    /// </summary>
    /// <param name="jobs">The project's jobs.</param>
    /// <param name="source">Where the numbers come from.</param>
    /// <returns>The summary.</returns>
    public static ScanSummary FromJobs(IEnumerable<JobRecord> jobs, string source)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(source);

        var entries = jobs
            .Where(j => !(j.State == JobState.Skipped && string.Equals(j.Error, JobStore.ProvidedByModError, StringComparison.Ordinal)))
            .Select(j => new Entry(j.State == JobState.Skipped ? j.Error ?? "Skipped" : null, j.Group, j.Kind, j.Target))
            .ToList();
        return Build(entries, source);
    }

    /// <summary>Gets the resolution bucket of a target size: the next power of two, as <c>512 px</c>, <c>1K</c>, <c>2K</c>, ...</summary>
    /// <param name="target">The target maximum dimension.</param>
    /// <returns>The bucket label.</returns>
    public static string ResolutionBucket(int target)
    {
        if (target <= 0)
        {
            return "unknown";
        }

        var bucket = BitOperations.RoundUpToPowerOf2((uint)target);
        return bucket >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bucket / 1024}K")
            : string.Create(CultureInfo.InvariantCulture, $"{bucket} px");
    }

    private static ScanSummary Build(List<Entry> entries, string source)
    {
        var queued = entries.Where(e => e.SkipReason is null).ToList();
        var skipped = entries.Where(e => e.SkipReason is not null).ToList();

        static long Bytes(IEnumerable<Entry> rows) => DriveAdvisor.EstimateOutputBytes(rows.Select(r => r.Target));

        var byReason = skipped
            .GroupBy(e => e.SkipReason!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SummaryRow(g.Key, g.Count(), null))
            .ToList();
        var byGroup = queued
            .GroupBy(e => e.Group ?? "(none)", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SummaryRow(g.Key, g.Count(), Bytes(g)))
            .ToList();
        var byKind = queued
            .GroupBy(e => e.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SummaryRow(g.Key, g.Count(), Bytes(g)))
            .ToList();
        var byResolution = queued
            .GroupBy(e => e.Target <= 0 ? 0u : BitOperations.RoundUpToPowerOf2((uint)e.Target))
            .OrderBy(g => g.Key)
            .Select(g => new SummaryRow(ResolutionBucket((int)Math.Min(g.Key, int.MaxValue)), g.Count(), Bytes(g)))
            .ToList();

        return new ScanSummary(entries.Count, queued.Count, skipped.Count, Bytes(queued), byReason, byGroup, byKind, byResolution, source);
    }

    private sealed record Entry(string? SkipReason, string? Group, string Kind, int Target);
}
