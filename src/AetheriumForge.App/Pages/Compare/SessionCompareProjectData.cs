using AetheriumForge.App.Services;
using AetheriumForge.Core.Common;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// <see cref="ICompareProjectData"/> over the shared <see cref="IProjectSession"/> and its <see cref="JobStore"/>.
/// </summary>
/// <remarks>The rules live in the project key/value table under <see cref="RulesJsonKey"/>, the key the scan writes.</remarks>
public sealed class SessionCompareProjectData : ICompareProjectData
{
    /// <summary>The project key that holds the rules JSON (same as the CLI's <c>ProjectKeys.RulesJson</c>).</summary>
    public const string RulesJsonKey = "rulesJson";

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
    /// <remarks>
    /// TODO(M5 look hashes): use <c>JobStore.FindDoneWithDifferentLookAsync</c> once the Core look work records a look hash per job.
    /// Until then every Done job of the group counts as different.
    /// </remarks>
    public async Task<IReadOnlyList<long>> FindDoneWithDifferentLookAsync(string group, LookSettings look, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(look);
        var jobs = await GetGroupJobsAsync(group, cancellationToken).ConfigureAwait(false);
        return [.. jobs.Where(job => job.State == JobState.Done).Select(job => job.Id)];
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO(M5 look hashes): replace the direct update with the job store's re-queue API when it lands in Core; <see cref="JobStore"/>
    /// has no public way to return Done jobs to Queued yet (its upsert deliberately keeps Done rows).
    /// </remarks>
    public async Task<int> RequeueDoneAsync(IReadOnlyCollection<long> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        var (project, _) = Require();
        if (jobIds.Count == 0)
        {
            return 0;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(project.Root, "forge.db"),
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = JobStore.BusyTimeoutMilliseconds / 1000,
        }.ToString();

        var requeued = 0;
        var connection = new SqliteConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE job SET state = 'Queued', error = NULL WHERE id = $id AND state = 'Done';";
                    var id = command.Parameters.Add("$id", SqliteType.Integer);
                    foreach (var jobId in jobIds)
                    {
                        id.Value = jobId;
                        requeued += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _session!.NotifyJobsChanged();
        return requeued;
    }

    private (ProjectInfo Project, JobStore Store) Require() =>
        _session?.Current is { } project && _session.Store is { } store
            ? (project, store)
            : throw new InvalidOperationException(PreviewEngine.NoProjectReason);
}
