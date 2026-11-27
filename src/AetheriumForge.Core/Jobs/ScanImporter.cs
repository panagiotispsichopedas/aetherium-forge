using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Jobs;

/// <summary>
/// Turns a <see cref="ScanResult"/> into job rows and applies it to a <see cref="JobStore"/> (IMPLEMENTATION_GUIDE §12, "Rescan").
/// </summary>
public static class ScanImporter
{
    /// <summary>
    /// Converts every scanned texture to a <see cref="JobRecord"/>: <see cref="JobState.Queued"/> when the rules upscale it,
    /// <see cref="JobState.Skipped"/> with the skip reason as the error otherwise.
    /// </summary>
    /// <param name="result">The scan result.</param>
    /// <returns>One job per texture, in scan order.</returns>
    public static IReadOnlyList<JobRecord> ToJobRecords(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return [.. result.Textures.Select(ToJobRecord)];
    }

    /// <summary>
    /// Upserts the scan into <paramref name="store"/>, then marks jobs that are no longer in the gap set as skipped
    /// ("now provided by a mod"). Finished jobs keep their state on upsert. Jobs of archives the scan could not read
    /// (<see cref="ScanResult.MissingArchives"/>) and running jobs are left alone. Runs in one transaction.
    /// </summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="result">The scan result.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The rows written and the jobs newly skipped by the rescan.</returns>
    public static Task<(int Upserted, int NowProvidedByMod)> ImportAsync(JobStore store, ScanResult result, CancellationToken cancellationToken = default) =>
        ImportAsync(store, result, new Dictionary<string, string>(), cancellationToken);

    /// <summary>
    /// Imports the scan like <see cref="ImportAsync(JobStore, ScanResult, CancellationToken)"/> and writes <paramref name="projectValues"/>
    /// in the same transaction, so a failed import leaves the project as it was.
    /// </summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="result">The scan result.</param>
    /// <param name="projectValues">Project values to write with the import (game path, rules, scan time).</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The rows written and the jobs newly skipped by the rescan.</returns>
    public static Task<(int Upserted, int NowProvidedByMod)> ImportAsync(
        JobStore store,
        ScanResult result,
        IReadOnlyDictionary<string, string> projectValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(projectValues);

        var gapPaths = result.Textures.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreadable = result.MissingArchives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return store.ImportScanAsync(ToJobRecords(result), gapPaths, unreadable, projectValues, cancellationToken);
    }

    private static JobRecord ToJobRecord(ScannedTexture texture)
    {
        var decision = texture.Decision;
        var header = texture.Header;

        return new JobRecord(
            Id: 0,
            Path: texture.Path,
            Archive: texture.SourceArchive,
            SourceWidth: header?.Width ?? 0,
            SourceHeight: header?.Height ?? 0,
            Format: header?.FormatName ?? "unknown",
            Kind: decision.Kind.ToString(),
            Group: decision.Group,
            Target: Math.Max(decision.TargetWidth, decision.TargetHeight),
            Model: decision.Model,
            State: decision.IsSkipped ? JobState.Skipped : JobState.Queued,
            Attempts: 0,
            Error: decision.IsSkipped ? decision.Skip.ToString() : null,
            Seconds: null,
            FinishedAt: null);
    }
}
