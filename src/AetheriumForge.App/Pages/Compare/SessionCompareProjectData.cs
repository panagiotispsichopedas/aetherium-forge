using AetheriumForge.App.Services;
using AetheriumForge.Core.Common;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// <see cref="ICompareProjectData"/> over the shared <see cref="IProjectSession"/> and its <see cref="JobStore"/>.
/// </summary>
/// <remarks>The rules live in the project key/value table under <see cref="RulesJsonKey"/>, the key the scan writes.</remarks>
public sealed class SessionCompareProjectData : ICompareProjectData
{
    /// <summary>The project key that holds the rules JSON (<see cref="ProjectKeys.RulesJson"/>, the key the scan writes).</summary>
    public const string RulesJsonKey = ProjectKeys.RulesJson;

    private readonly IProjectSession? _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCompareProjectData"/> class.
    /// </summary>
    /// <param name="session">The session, or <see langword="null"/> when none is registered yet (no project).</param>
    public SessionCompareProjectData(IProjectSession? session)
    {
        _session = session;
        if (session is not null)
        {
            session.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool HasProject => _session?.Current is not null && _session.Store is not null;

    /// <inheritdoc />
    public async Task<ForgeRules> LoadRulesAsync(CancellationToken cancellationToken)
    {
        var (project, store) = Require();
        var json = await store.GetProjectValueAsync(RulesJsonKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? project.Rules : ForgeRules.FromJson(json);
    }

    /// <inheritdoc />
    public async Task SaveRulesAsync(ForgeRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var (_, store) = Require();
        await store.SetProjectValueAsync(RulesJsonKey, rules.ToJson(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> SearchFinishedAsync(string text, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var (_, store) = Require();
        var needle = text.Trim().Replace('/', '\\');
        var matches = new List<JobRecord>();
        await foreach (var job in store.GetJobsAsync(JobState.Done, cancellationToken).ConfigureAwait(false))
        {
            if (needle.Length == 0 || job.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(job);
            }
        }

        return [.. matches.OrderBy(job => job.Path, GamePath.Comparer).Take(limit)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> GetGroupJobsAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var (_, store) = Require();
        var jobs = new List<JobRecord>();
        await foreach (var job in store.GetJobsAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (job.State != JobState.Skipped && string.Equals(job.Group, group, StringComparison.OrdinalIgnoreCase))
            {
                jobs.Add(job);
            }
        }

        return [.. jobs.OrderBy(job => job.Id)];
    }

    /// <inheritdoc />
    public Task<LookOverrides?> GetTextureLookAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Require().Store.GetTextureLookAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetTextureLookAsync(string path, LookOverrides look, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(look);
        var (_, store) = Require();
        if (look.IsEmpty)
        {
            await store.RemoveTextureLookAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await store.SetTextureLookAsync(path, look, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobRecord>> FindDoneAffectedByLookChangeAsync(Func<JobRecord, LookOverrides?, LookSettings> effectiveLookAfterChange, string? group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effectiveLookAfterChange);
        return Require().Store.FindDoneAffectedByLookChangeAsync(effectiveLookAfterChange, group, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Uses <see cref="JobStore.SetStateAsync(IReadOnlyCollection{long}, JobState, string?, IReadOnlyCollection{JobState}?, CancellationToken)"/> from Done only.</remarks>
    public async Task<int> RequeueDoneAsync(IReadOnlyCollection<long> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        var (_, store) = Require();
        if (jobIds.Count == 0)
        {
            return 0;
        }

        var requeued = await store.SetStateAsync(jobIds, JobState.Queued, null, [JobState.Done], cancellationToken).ConfigureAwait(false);
        _session!.NotifyJobsChanged();
        return requeued;
    }

    private (ProjectInfo Project, JobStore Store) Require() =>
        _session?.Current is { } project && _session.Store is { } store
            ? (project, store)
            : throw new InvalidOperationException(PreviewEngine.NoProjectReason);
}
