using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.App.Services;

/// <summary>
/// The project session as the Scan page sees it: <see cref="IProjectSession"/> plus creating a project from a scan, the recent list and
/// reopening the last project. Queue and Run only need <see cref="IProjectSession"/>.
/// </summary>
public interface IScanProjectSession : IProjectSession
{
    /// <summary>Gets the recently opened project folders, most recent first.</summary>
    IReadOnlyList<string> RecentProjects { get; }

    /// <summary>
    /// Imports a scan into a project, creating the project when the folder has none, and makes it the open project. Stores the game path,
    /// the store, the rules and the scan time in the project table (like <c>aetherium-forge scan</c>), all in one transaction. Raises <see cref="IProjectSession.Changed"/>;
    /// the caller raises <see cref="IProjectSession.JobsChanged"/> through <see cref="IProjectSession.NotifyJobsChanged"/>.
    /// </summary>
    /// <param name="request">The project folder, game, rules and scan.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The import counts and the project's job counts by state.</returns>
    Task<ScanImportResult> ApplyScanAsync(ScanImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one value of the open project's key/value table.</summary>
    /// <param name="key">The key (see <c>ProjectKeys</c>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when no project is open or the key is not set.</returns>
    Task<string?> GetProjectValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads the open project's jobs.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every job, or an empty list when no project is open.</returns>
    Task<IReadOnlyList<JobRecord>> GetJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reopens the most recent project that still exists.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns><see langword="true"/> when a project was opened.</returns>
    Task<bool> ReopenLastAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What to import with <see cref="IScanProjectSession.ApplyScanAsync"/>.
/// </summary>
/// <param name="ProjectFolder">The project folder; created when missing. Must be outside the game folder.</param>
/// <param name="Game">The scanned game.</param>
/// <param name="Rules">The rules the scan used.</param>
/// <param name="Result">The scan.</param>
public sealed record ScanImportRequest(string ProjectFolder, GameInstall Game, ForgeRules Rules, ScanResult Result);

/// <summary>
/// The outcome of <see cref="IScanProjectSession.ApplyScanAsync"/>.
/// </summary>
/// <param name="DatabasePath">The project's <c>forge.db</c>.</param>
/// <param name="Upserted">Rows written.</param>
/// <param name="NowProvidedByMod">Jobs newly skipped because a mod now provides them.</param>
/// <param name="Counts">The project's jobs by state after the import.</param>
/// <param name="UnreadableArchives">Archives the scan could not read; their textures are missing from the scan and their jobs were left unchanged.</param>
public sealed record ScanImportResult(
    string DatabasePath,
    int Upserted,
    int NowProvidedByMod,
    IReadOnlyDictionary<JobState, int> Counts,
    IReadOnlyList<string> UnreadableArchives);
