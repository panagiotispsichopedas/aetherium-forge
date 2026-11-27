using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Jobs;

/// <summary>
/// Queue-screen queries and bulk edits: filtered and sorted reads, counts, distinct filter values, and multi-row state and model
/// changes. Every value is a SQL parameter; only fixed column names chosen from <see cref="JobSortColumn"/> enter the SQL text.
/// </summary>
public sealed partial class JobStore
{
    private const char LikeEscape = '!';

    /// <summary>
    /// Streams the jobs that match <paramref name="query"/>, in its sort order and page.
    /// </summary>
    /// <param name="query">Filters, sort and page.</param>
    /// <param name="ct">Cancels the enumeration.</param>
    /// <returns>The matching jobs.</returns>
    public async IAsyncEnumerable<JobRecord> QueryJobsAsync(JobQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Limit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);
        }

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var sql = new StringBuilder($"SELECT {JobColumns} FROM job");
                AppendWhere(sql, command, query, includeStates: true);
                var direction = query.Descending ? "DESC" : "ASC";
                sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {SortExpression(query.SortBy)} {direction}, id {direction}");
                if (query.Limit is not null || query.Offset > 0)
                {
                    sql.Append(" LIMIT $limit OFFSET $offset");
                    command.Parameters.AddWithValue("$limit", query.Limit ?? -1);
                    command.Parameters.AddWithValue("$offset", query.Offset);
                }

                command.CommandText = sql.Append(';').ToString();
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        yield return ReadJob(reader);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Counts the jobs that match <paramref name="query"/>'s filters (its sort and page are ignored).
    /// </summary>
    /// <param name="query">The filters.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The number of matching jobs.</returns>
    public async Task<int> CountJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var sql = new StringBuilder("SELECT COUNT(*) FROM job");
                AppendWhere(sql, command, query, includeStates: true);
                command.CommandText = sql.Append(';').ToString();

                return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Counts jobs per state under <paramref name="query"/>'s group, kind and text filters (its state set is ignored), for the
    /// state filter's counts.
    /// </summary>
    /// <param name="query">The filters.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A count for every <see cref="JobState"/>, including states with no jobs (0).</returns>
    public async Task<IReadOnlyDictionary<JobState, int>> CountByStateAsync(JobQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var counts = Enum.GetValues<JobState>().ToDictionary(state => state, _ => 0);
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var sql = new StringBuilder("SELECT state, COUNT(*) FROM job");
                AppendWhere(sql, command, query, includeStates: false);
                command.CommandText = sql.Append(" GROUP BY state;").ToString();
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        counts[ParseState(reader.GetString(0))] = reader.GetInt32(1);
                    }
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// Sets the state and error of several jobs in one transaction. A <see cref="JobState.Running"/> job is never changed.
    /// </summary>
    /// <param name="ids">The job ids; unknown ids are ignored.</param>
    /// <param name="state">The new state; not <see cref="JobState.Running"/> (only the pipeline takes jobs).</param>
    /// <param name="error">The new error, or <see langword="null"/> to clear it.</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of jobs changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> is <see cref="JobState.Running"/>.</exception>
    public Task<int> SetStateAsync(IReadOnlyCollection<long> ids, JobState state, string? error, CancellationToken ct = default) =>
        SetStateAsync(ids, state, error, fromStates: null, ct);

    /// <summary>
    /// Sets the state and error of those of several jobs that are currently in one of <paramref name="fromStates"/>, in one
    /// transaction, so a rule such as "Retry: Failed → Queued" holds even if a row changed since it was read. A
    /// <see cref="JobState.Running"/> job is never changed. Moving to <see cref="JobState.Queued"/> also clears the job's time and
    /// finish timestamp, which describe a result that no longer stands; its attempt count is kept.
    /// </summary>
    /// <param name="ids">The job ids; unknown ids are ignored.</param>
    /// <param name="state">The new state; not <see cref="JobState.Running"/> (only the pipeline takes jobs).</param>
    /// <param name="error">The new error, or <see langword="null"/> to clear it.</param>
    /// <param name="fromStates">Only change jobs in these states; <see langword="null"/> allows any state except Running.</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of jobs changed.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> is <see cref="JobState.Running"/>.</exception>
    public async Task<int> SetStateAsync(
        IReadOnlyCollection<long> ids,
        JobState state,
        string? error,
        IReadOnlyCollection<JobState>? fromStates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (state == JobState.Running)
        {
            throw new ArgumentException("Only the pipeline may mark a job Running.", nameof(state));
        }

        var clearResult = state == JobState.Queued ? ", seconds = NULL, finished_at = NULL" : string.Empty;
        var sql = new StringBuilder($"UPDATE job SET state = $state, error = $error{clearResult} WHERE id = $id AND state <> 'Running'");
        var parameters = new List<(string Name, object Value)> { ("$state", state.ToString()), ("$error", (object?)error ?? DBNull.Value) };
        if (fromStates is not null)
        {
            if (fromStates.Count == 0)
            {
                return 0;
            }

            sql.Append(" AND state IN (");
            var index = 0;
            foreach (var from in fromStates.Distinct())
            {
                var name = $"$from{index.ToString(CultureInfo.InvariantCulture)}";
                sql.Append(index == 0 ? name : $", {name}");
                parameters.Add((name, from.ToString()));
                index++;
            }

            sql.Append(')');
        }

        return await UpdateManyAsync(ids, sql.Append(';').ToString(), parameters, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the model of several jobs in one transaction. A <see cref="JobState.Running"/> job is never changed.
    /// </summary>
    /// <param name="ids">The job ids; unknown ids are ignored.</param>
    /// <param name="model">The model name (as listed by the model catalog).</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of jobs changed.</returns>
    public async Task<int> SetModelAsync(IReadOnlyCollection<long> ids, string model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return await UpdateManyAsync(ids, "UPDATE job SET model = $model WHERE id = $id AND state <> 'Running';", [("$model", model)], ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the distinct rule groups of all jobs, sorted, for the group filter. Jobs without a group are not represented.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The groups.</returns>
    public Task<IReadOnlyList<string>> GetDistinctGroupsAsync(CancellationToken ct = default) =>
        DistinctAsync("SELECT DISTINCT grp FROM job WHERE grp IS NOT NULL AND grp <> '' ORDER BY grp;", ct);

    /// <summary>
    /// Lists the distinct texture kinds of all jobs, sorted, for the kind filter.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The kinds.</returns>
    public Task<IReadOnlyList<string>> GetDistinctKindsAsync(CancellationToken ct = default) =>
        DistinctAsync("SELECT DISTINCT kind FROM job WHERE kind IS NOT NULL AND kind <> '' ORDER BY kind;", ct);

    /// <summary>
    /// Escapes a text filter for <c>LIKE '%…%' ESCAPE '!'</c>: normalises separators and case like a game path and makes
    /// <c>%</c>, <c>_</c> and <c>!</c> literal.
    /// </summary>
    /// <param name="text">The user's text.</param>
    /// <returns>The LIKE pattern.</returns>
    internal static string ToContainsPattern(string text)
    {
        var normalised = text.Replace('/', '\\').Trim().ToLowerInvariant();
        var escaped = new StringBuilder(normalised.Length + 2).Append('%');
        foreach (var c in normalised)
        {
            if (c is LikeEscape or '%' or '_')
            {
                escaped.Append(LikeEscape);
            }

            escaped.Append(c);
        }

        return escaped.Append('%').ToString();
    }

    private static string SortExpression(JobSortColumn column) => column switch
    {
        JobSortColumn.Id => "id",
        JobSortColumn.Path => "path",
        JobSortColumn.Kind => "kind",
        JobSortColumn.Group => "grp",
        JobSortColumn.Source => "(COALESCE(src_w, 0) * COALESCE(src_h, 0))",
        JobSortColumn.Target => "target",
        JobSortColumn.Model => "model",
        JobSortColumn.State => "(CASE state WHEN 'Queued' THEN 0 WHEN 'Running' THEN 1 WHEN 'Done' THEN 2 WHEN 'Failed' THEN 3 WHEN 'Skipped' THEN 4 ELSE 5 END)",
        JobSortColumn.Attempts => "attempts",
        JobSortColumn.Seconds => "seconds",
        JobSortColumn.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown sort column."),
    };

    private static void AppendWhere(StringBuilder sql, SqliteCommand command, JobQuery query, bool includeStates)
    {
        var conditions = new List<string>();
        if (includeStates && query.States is { } states)
        {
            if (states.Count == 0)
            {
                conditions.Add("0");
            }
            else
            {
                var names = new List<string>();
                foreach (var state in states.Order())
                {
                    var name = $"$state{names.Count.ToString(CultureInfo.InvariantCulture)}";
                    names.Add(name);
                    command.Parameters.AddWithValue(name, state.ToString());
                }

                conditions.Add($"state IN ({string.Join(", ", names)})");
            }
        }

        if (query.Group is { } group)
        {
            conditions.Add("grp = $grp");
            command.Parameters.AddWithValue("$grp", group);
        }

        if (query.Kind is { } kind)
        {
            conditions.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", kind);
        }

        if (!string.IsNullOrWhiteSpace(query.PathContains))
        {
            conditions.Add($"path LIKE $text ESCAPE '{LikeEscape}'");
            command.Parameters.AddWithValue("$text", ToContainsPattern(query.PathContains));
        }

        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        }
    }

    private async Task<int> UpdateManyAsync(IReadOnlyCollection<long> ids, string sql, IReadOnlyList<(string Name, object Value)> parameters, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    foreach (var (name, value) in parameters)
                    {
                        command.Parameters.AddWithValue(name, value);
                    }

                    var id = command.Parameters.Add("$id", SqliteType.Integer);
                    var affected = 0;
                    foreach (var jobId in ids.Distinct())
                    {
                        id.Value = jobId;
                        affected += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return affected;
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> DistinctAsync(string sql, CancellationToken ct)
    {
        var values = new List<string>();
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        values.Add(reader.GetString(0));
                    }
                }
            }
        }

        return values;
    }
}
