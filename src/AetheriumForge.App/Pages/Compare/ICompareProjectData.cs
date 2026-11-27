using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The project data the Compare page reads and writes: the rules and the job rows.
/// </summary>
public interface ICompareProjectData
{
    /// <summary>Raised when a project was opened, closed or rescanned.</summary>
    event EventHandler? Changed;

    /// <summary>Gets whether a project is open.</summary>
    bool HasProject { get; }

    /// <summary>Loads the project's current rules (the stored rules JSON, else the rules the project was opened with).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rules.</returns>
    Task<ForgeRules> LoadRulesAsync(CancellationToken cancellationToken);

    /// <summary>Stores the project's rules (the same key the scan writes).</summary>
    /// <param name="rules">The rules.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the rules are stored.</returns>
    Task SaveRulesAsync(ForgeRules rules, CancellationToken cancellationToken);

    /// <summary>Finds finished jobs whose path contains <paramref name="text"/>.</summary>
    /// <param name="text">The search text (case-insensitive); empty matches every job.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The matching jobs, by path.</returns>
    Task<IReadOnlyList<JobRecord>> SearchFinishedAsync(string text, int limit, CancellationToken cancellationToken);

    /// <summary>Gets every job of a group that is not skipped (candidates for samples).</summary>
    /// <param name="group">The group name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The jobs, ordered by id.</returns>
    Task<IReadOnlyList<JobRecord>> GetGroupJobsAsync(string group, CancellationToken cancellationToken);

    /// <summary>Reads the "This texture" look override of a path (the <c>texture_look</c> table).</summary>
    /// <param name="path">The canonical path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The override, or <see langword="null"/> when the texture has none.</returns>
    Task<LookOverrides?> GetTextureLookAsync(string path, CancellationToken cancellationToken);

    /// <summary>Stores a "This texture" look override; an empty override removes it.</summary>
    /// <param name="path">The canonical path.</param>
    /// <param name="look">The override.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task SetTextureLookAsync(string path, LookOverrides look, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the Done jobs whose effective look hash changes under <paramref name="effectiveLookAfterChange"/>
    /// (<see cref="JobStore.FindDoneAffectedByLookChangeAsync"/>).
    /// </summary>
    /// <param name="effectiveLookAfterChange">Resolves a job's effective look after the change, given the job and its stored texture override.</param>
    /// <param name="group">Only consider this group, or <see langword="null"/> for every group.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The affected jobs in id order.</returns>
    Task<IReadOnlyList<JobRecord>> FindDoneAffectedByLookChangeAsync(Func<JobRecord, LookOverrides?, LookSettings> effectiveLookAfterChange, string? group, CancellationToken cancellationToken);

    /// <summary>Returns <see cref="JobState.Done"/> jobs to <see cref="JobState.Queued"/>, then raises the session's jobs-changed event.</summary>
    /// <param name="jobIds">The job ids; jobs that are no longer Done are left alone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of jobs re-queued.</returns>
    Task<int> RequeueDoneAsync(IReadOnlyCollection<long> jobIds, CancellationToken cancellationToken);
}
