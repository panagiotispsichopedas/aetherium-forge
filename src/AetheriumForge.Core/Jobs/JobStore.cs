using System.Globalization;
using System.Runtime.CompilerServices;
using AetheriumForge.Core.Common;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Jobs;

/// <summary>
/// SQLite-backed store of a project's settings, texture jobs and runs (IMPLEMENTATION_GUIDE §12).
/// </summary>
/// <remarks>
/// Every operation opens its own pooled connection, so the store is safe to call from several pipeline workers at once.
/// The database runs in WAL mode; SQLite serialises writers and Microsoft.Data.Sqlite retries while the database is busy.
/// </remarks>
public sealed class JobStore : IAsyncDisposable
{
    /// <summary>Error recorded on jobs that a rescan removed from the gap set.</summary>
    public const string ProvidedByModError = "now provided by a mod";

    /// <summary>How long a connection waits for another writer's lock before failing with <c>SQLITE_BUSY</c>, in milliseconds.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS project (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS job (
          id INTEGER PRIMARY KEY,
          path TEXT UNIQUE NOT NULL,
          archive TEXT NOT NULL,
          src_w INT, src_h INT, format TEXT, kind TEXT,
          grp TEXT, target INT, model TEXT,
          state TEXT NOT NULL,
          attempts INT DEFAULT 0, error TEXT,
          seconds REAL, finished_at TEXT
        );
        CREATE INDEX IF NOT EXISTS job_state ON job(state);
        CREATE TABLE IF NOT EXISTS run (id INTEGER PRIMARY KEY, started TEXT, ended TEXT, done INT, failed INT);
        """;

    private const string JobColumns = "id, path, archive, src_w, src_h, format, kind, grp, target, model, state, attempts, error, seconds, finished_at";

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private JobStore(string connectionString, TimeProvider timeProvider)
    {
        _connectionString = connectionString;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Opens (or creates) a job store, creating the schema when missing and switching the database to WAL mode.
    /// </summary>
    /// <param name="databasePath">Path of the SQLite database file.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The open store.</returns>
    public static Task<JobStore> OpenAsync(string databasePath, CancellationToken ct = default) =>
        OpenAsync(databasePath, TimeProvider.System, ct);

    /// <summary>
    /// Opens (or creates) a job store with an explicit clock for the timestamps it writes.
    /// </summary>
    /// <param name="databasePath">Path of the SQLite database file.</param>
    /// <param name="timeProvider">Clock for <c>finished_at</c>, <c>started</c> and <c>ended</c>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The open store.</returns>
    public static async Task<JobStore> OpenAsync(string databasePath, TimeProvider timeProvider, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        var store = new JobStore(connectionString, timeProvider);
        var connection = await store.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, SchemaSql, ct).ConfigureAwait(false);
        }

        return store;
    }

    /// <summary>
    /// Reads a project setting.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The value, or <see langword="null"/> when the key is not set.</returns>
    public async Task<string?> GetProjectValueAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT value FROM project WHERE key = $key;";
                command.Parameters.AddWithValue("$key", key);
                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                return result is null or DBNull ? null : (string)result;
            }
        }
    }

    /// <summary>
    /// Writes (inserts or replaces) a project setting.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The value.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the value is stored.</returns>
    public async Task SetProjectValueAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        await ExecuteNonQueryAsync(
            "INSERT INTO project (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            [("$key", key), ("$value", value)],
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts new jobs and updates existing ones, matched by canonical path, in a single transaction.
    /// </summary>
    /// <param name="jobs">The jobs; <see cref="JobRecord.Id"/> is ignored and <see cref="JobRecord.Path"/> is normalised.</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of rows inserted or updated.</returns>
    /// <remarks>
    /// An existing job keeps its <c>attempts</c> count. An existing <see cref="JobState.Done"/> job also keeps its state,
    /// error, seconds and finish time, so a rescan never repeats finished work; only its descriptive columns are refreshed.
    /// Any other existing job takes the incoming state and error.
    /// </remarks>
    public async Task<int> UpsertJobsAsync(IEnumerable<JobRecord> jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        const string sql = """
            INSERT INTO job (path, archive, src_w, src_h, format, kind, grp, target, model, state, attempts, error, seconds, finished_at)
            VALUES ($path, $archive, $src_w, $src_h, $format, $kind, $grp, $target, $model, $state, $attempts, $error, $seconds, $finished_at)
            ON CONFLICT(path) DO UPDATE SET
              archive = excluded.archive,
              src_w = excluded.src_w,
              src_h = excluded.src_h,
              format = excluded.format,
              kind = excluded.kind,
              grp = excluded.grp,
              target = excluded.target,
              model = excluded.model,
              state = CASE WHEN job.state = 'Done' THEN job.state ELSE excluded.state END,
              error = CASE WHEN job.state = 'Done' THEN job.error ELSE excluded.error END,
              seconds = CASE WHEN job.state = 'Done' THEN job.seconds ELSE excluded.seconds END,
              finished_at = CASE WHEN job.state = 'Done' THEN job.finished_at ELSE excluded.finished_at END;
            """;

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
                    var path = command.Parameters.Add("$path", SqliteType.Text);
                    var archive = command.Parameters.Add("$archive", SqliteType.Text);
                    var sourceWidth = command.Parameters.Add("$src_w", SqliteType.Integer);
                    var sourceHeight = command.Parameters.Add("$src_h", SqliteType.Integer);
                    var format = command.Parameters.Add("$format", SqliteType.Text);
                    var kind = command.Parameters.Add("$kind", SqliteType.Text);
                    var group = command.Parameters.Add("$grp", SqliteType.Text);
                    var target = command.Parameters.Add("$target", SqliteType.Integer);
                    var model = command.Parameters.Add("$model", SqliteType.Text);
                    var state = command.Parameters.Add("$state", SqliteType.Text);
                    var attempts = command.Parameters.Add("$attempts", SqliteType.Integer);
                    var error = command.Parameters.Add("$error", SqliteType.Text);
                    var seconds = command.Parameters.Add("$seconds", SqliteType.Real);
                    var finishedAt = command.Parameters.Add("$finished_at", SqliteType.Text);

                    var affected = 0;
                    foreach (var job in jobs)
                    {
                        ArgumentNullException.ThrowIfNull(job, nameof(jobs));

                        path.Value = GamePath.Normalize(job.Path);
                        archive.Value = job.Archive;
                        sourceWidth.Value = job.SourceWidth;
                        sourceHeight.Value = job.SourceHeight;
                        format.Value = job.Format;
                        kind.Value = job.Kind;
                        group.Value = (object?)job.Group ?? DBNull.Value;
                        target.Value = job.Target;
                        model.Value = (object?)job.Model ?? DBNull.Value;
                        state.Value = job.State.ToString();
                        attempts.Value = job.Attempts;
                        error.Value = (object?)job.Error ?? DBNull.Value;
                        seconds.Value = (object?)job.Seconds ?? DBNull.Value;
                        finishedAt.Value = job.FinishedAt is { } finished ? FormatTimestamp(finished) : DBNull.Value;

                        affected += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return affected;
                }
            }
        }
    }

    /// <summary>
    /// Streams jobs in id order, optionally filtered by state.
    /// </summary>
    /// <param name="state">Only return jobs in this state; <see langword="null"/> returns every job.</param>
    /// <param name="ct">Cancels the enumeration.</param>
    /// <returns>The jobs.</returns>
    public async IAsyncEnumerable<JobRecord> GetJobsAsync(JobState? state = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                if (state is { } filter)
                {
                    command.CommandText = $"SELECT {JobColumns} FROM job WHERE state = $state ORDER BY id;";
                    command.Parameters.AddWithValue("$state", filter.ToString());
                }
                else
                {
                    command.CommandText = $"SELECT {JobColumns} FROM job ORDER BY id;";
                }

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
    /// Counts jobs per state.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A count for every <see cref="JobState"/>, including states with no jobs (0).</returns>
    public async Task<IReadOnlyDictionary<JobState, int>> CountByStateAsync(CancellationToken ct = default)
    {
        var counts = Enum.GetValues<JobState>().ToDictionary(state => state, _ => 0);

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT state, COUNT(*) FROM job GROUP BY state;";
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
    /// Marks a <see cref="JobState.Queued"/> job as taken by the pipeline.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="KeyNotFoundException">No job has this id.</exception>
    /// <exception cref="InvalidOperationException">The job is not <see cref="JobState.Queued"/> (for example a rescan skipped it).</exception>
    public async Task MarkRunningAsync(long id, CancellationToken ct = default)
    {
        if (await TryMarkRunningAsync(id, ct).ConfigureAwait(false))
        {
            return;
        }

        var exists = await ExecuteScalarAsync("SELECT COUNT(*) FROM job WHERE id = $id;", [("$id", id)], ct).ConfigureAwait(false);
        if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
        {
            throw new KeyNotFoundException($"Job {id} does not exist.");
        }

        throw new InvalidOperationException($"Job {id} is no longer queued.");
    }

    /// <summary>
    /// Marks a job as taken by the pipeline only if it is still <see cref="JobState.Queued"/>. A job that a rescan skipped (or that
    /// another writer changed) since it was read is left alone, so a rescan during a run wins.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the job was Queued and is now Running; <see langword="false"/> when it is missing or not Queued.</returns>
    public async Task<bool> TryMarkRunningAsync(long id, CancellationToken ct = default) =>
        await ExecuteNonQueryAsync("UPDATE job SET state = 'Running' WHERE id = $id AND state = 'Queued';", [("$id", id)], ct).ConfigureAwait(false) == 1;

    /// <summary>
    /// Marks a job as finished successfully and clears its error.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="seconds">Processing time in seconds.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="KeyNotFoundException">No job has this id.</exception>
    public Task MarkDoneAsync(long id, double seconds, CancellationToken ct = default) =>
        UpdateJobAsync(
            id,
            "UPDATE job SET state = 'Done', error = NULL, seconds = $seconds, finished_at = $finished_at WHERE id = $id;",
            [("$seconds", seconds), ("$finished_at", FormatTimestamp(_timeProvider.GetUtcNow()))],
            ct);

    /// <summary>
    /// Marks a job as failed, records the error and increments its attempt count.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="error">The error message.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="KeyNotFoundException">No job has this id.</exception>
    public Task MarkFailedAsync(long id, string error, CancellationToken ct = default) => MarkFailedAsync(id, error, 1, ct);

    /// <summary>
    /// Marks a job as failed, records the error and adds the attempts made (for example every in-run retry) to its attempt count.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="error">The error message.</param>
    /// <param name="attempts">Attempts made since the last record; 0 for a job that was rejected without being attempted.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="KeyNotFoundException">No job has this id.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempts"/> is negative.</exception>
    public Task MarkFailedAsync(long id, string error, int attempts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);

        return UpdateJobAsync(
            id,
            "UPDATE job SET state = 'Failed', error = $error, attempts = COALESCE(attempts, 0) + $attempts, finished_at = $finished_at WHERE id = $id;",
            [("$error", error), ("$attempts", attempts), ("$finished_at", FormatTimestamp(_timeProvider.GetUtcNow()))],
            ct);
    }

    /// <summary>
    /// Crash recovery (IMPLEMENTATION_GUIDE §11): returns every job that is not <see cref="JobState.Done"/>,
    /// <see cref="JobState.Failed"/> or <see cref="JobState.Skipped"/> to <see cref="JobState.Queued"/>.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The number of jobs that changed state.</returns>
    public async Task<int> RequeueInterruptedAsync(CancellationToken ct = default) =>
        await ExecuteNonQueryAsync(
            "UPDATE job SET state = 'Queued' WHERE state NOT IN ('Queued', 'Done', 'Failed', 'Skipped');",
            [],
            ct).ConfigureAwait(false);

    /// <summary>
    /// Applies a rescan: every job whose path is not in the new gap set becomes <see cref="JobState.Skipped"/>
    /// with the error <see cref="ProvidedByModError"/>.
    /// </summary>
    /// <param name="currentGapPaths">The paths of the recomputed gap set (normalised before comparison).</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of jobs newly skipped.</returns>
    /// <remarks>New gaps are added, as <see cref="JobState.Queued"/>, through <see cref="UpsertJobsAsync"/>.</remarks>
    public async Task<int> ApplyRescanAsync(IReadOnlySet<string> currentGapPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentGapPaths);

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await ExecuteAsync(connection, "CREATE TEMP TABLE IF NOT EXISTS rescan_gap (path TEXT PRIMARY KEY); DELETE FROM temp.rescan_gap;", ct, transaction)
                    .ConfigureAwait(false);

                var insert = connection.CreateCommand();
                await using (insert.ConfigureAwait(false))
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO temp.rescan_gap (path) VALUES ($path);";
                    var path = insert.Parameters.Add("$path", SqliteType.Text);
                    foreach (var gapPath in currentGapPaths)
                    {
                        path.Value = GamePath.Normalize(gapPath);
                        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                int skipped;
                var update = connection.CreateCommand();
                await using (update.ConfigureAwait(false))
                {
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE job SET state = 'Skipped', error = $error
                        WHERE state <> 'Skipped' AND path NOT IN (SELECT path FROM temp.rescan_gap);
                        """;
                    update.Parameters.AddWithValue("$error", ProvidedByModError);
                    skipped = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await ExecuteAsync(connection, "DROP TABLE temp.rescan_gap;", ct, transaction).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return skipped;
            }
        }
    }

    /// <summary>
    /// Applies rule changes (from <see cref="Rules.RulesApplier.Preview"/>) in a single transaction: each row takes its new group, target,
    /// model, state and error. A row is updated only while it is still in the change's <see cref="Rules.JobRuleChange.OldState"/>, and
    /// <see cref="JobState.Running"/> rows are never touched, so a row the pipeline took meanwhile is left alone.
    /// </summary>
    /// <param name="changes">The changes.</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of rows updated.</returns>
    /// <remarks>A row leaving <see cref="JobState.Done"/> loses its processing time and finish time; its attempt count is kept.</remarks>
    /// <exception cref="ArgumentException">A change targets a state other than <see cref="JobState.Queued"/> or <see cref="JobState.Skipped"/>.</exception>
    public Task<int> ApplyRuleChangesAsync(IEnumerable<Rules.JobRuleChange> changes, CancellationToken ct = default) =>
        ApplyRuleChangesAsync(changes, null, ct);

    /// <summary>
    /// Applies rule changes like <see cref="ApplyRuleChangesAsync(IEnumerable{Rules.JobRuleChange}, CancellationToken)"/> and, in the same
    /// transaction, stores <paramref name="rulesJson"/> under <see cref="Rules.RulesApplier.RulesProjectKey"/>: either both are written or neither.
    /// </summary>
    /// <param name="changes">The changes.</param>
    /// <param name="rulesJson">The rules JSON to store, or <see langword="null"/> to leave the stored rules alone.</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of rows updated.</returns>
    /// <exception cref="ArgumentException">A change targets a state other than <see cref="JobState.Queued"/> or <see cref="JobState.Skipped"/>.</exception>
    public async Task<int> ApplyRuleChangesAsync(IEnumerable<Rules.JobRuleChange> changes, string? rulesJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        const string sql = """
            UPDATE job SET
              grp = $grp, target = $target, model = $model, state = $state, error = $error,
              seconds = CASE WHEN state = 'Done' THEN NULL ELSE seconds END,
              finished_at = CASE WHEN state = 'Done' THEN NULL ELSE finished_at END
            WHERE id = $id AND state = $old AND state <> 'Running';
            """;

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
                    var id = command.Parameters.Add("$id", SqliteType.Integer);
                    var old = command.Parameters.Add("$old", SqliteType.Text);
                    var group = command.Parameters.Add("$grp", SqliteType.Text);
                    var target = command.Parameters.Add("$target", SqliteType.Integer);
                    var model = command.Parameters.Add("$model", SqliteType.Text);
                    var state = command.Parameters.Add("$state", SqliteType.Text);
                    var error = command.Parameters.Add("$error", SqliteType.Text);

                    var affected = 0;
                    foreach (var change in changes)
                    {
                        ArgumentNullException.ThrowIfNull(change, nameof(changes));
                        if (change.NewState is not (JobState.Queued or JobState.Skipped))
                        {
                            throw new ArgumentException($"Job {change.Id}: a rule change can only queue or skip a job, not set it to {change.NewState}.", nameof(changes));
                        }

                        id.Value = change.Id;
                        old.Value = change.OldState.ToString();
                        group.Value = (object?)change.Group ?? DBNull.Value;
                        target.Value = change.Target;
                        model.Value = (object?)change.Model ?? DBNull.Value;
                        state.Value = change.NewState.ToString();
                        error.Value = (object?)change.Error ?? DBNull.Value;

                        affected += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    if (rulesJson is not null)
                    {
                        var setRules = connection.CreateCommand();
                        await using (setRules.ConfigureAwait(false))
                        {
                            setRules.Transaction = transaction;
                            setRules.CommandText = "INSERT INTO project (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                            setRules.Parameters.AddWithValue("$key", Rules.RulesApplier.RulesProjectKey);
                            setRules.Parameters.AddWithValue("$value", rulesJson);
                            await setRules.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return affected;
                }
            }
        }
    }

    /// <summary>
    /// Records the start of a processing run.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The new run id.</returns>
    public async Task<long> StartRunAsync(CancellationToken ct = default)
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "INSERT INTO run (started, done, failed) VALUES ($started, 0, 0) RETURNING id;";
                command.Parameters.AddWithValue("$started", FormatTimestamp(_timeProvider.GetUtcNow()));
                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                return Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Records the end of a processing run.
    /// </summary>
    /// <param name="runId">The id returned by <see cref="StartRunAsync"/>.</param>
    /// <param name="done">Jobs finished in this run.</param>
    /// <param name="failed">Jobs failed in this run.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the run is stored.</returns>
    /// <exception cref="KeyNotFoundException">No run has this id.</exception>
    public async Task EndRunAsync(long runId, int done, int failed, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(done);
        ArgumentOutOfRangeException.ThrowIfNegative(failed);

        var affected = await ExecuteNonQueryAsync(
            "UPDATE run SET ended = $ended, done = $done, failed = $failed WHERE id = $id;",
            [("$ended", FormatTimestamp(_timeProvider.GetUtcNow())), ("$done", done), ("$failed", failed), ("$id", runId)],
            ct).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Run {runId} does not exist.");
        }
    }

    /// <summary>
    /// Releases the pooled connections so the database file can be moved or deleted.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            using var connection = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(connection);
        }

        return ValueTask.CompletedTask;
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static JobState ParseState(string value) => Enum.Parse<JobState>(value, ignoreCase: false);

    private static JobRecord ReadJob(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Path: reader.GetString(1),
        Archive: reader.GetString(2),
        SourceWidth: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
        SourceHeight: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
        Format: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        Kind: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        Group: reader.IsDBNull(7) ? null : reader.GetString(7),
        Target: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
        Model: reader.IsDBNull(9) ? null : reader.GetString(9),
        State: ParseState(reader.GetString(10)),
        Attempts: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
        Error: reader.IsDBNull(12) ? null : reader.GetString(12),
        Seconds: reader.IsDBNull(13) ? null : reader.GetDouble(13),
        FinishedAt: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateJobAsync(long id, string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        var affected = await ExecuteNonQueryAsync(sql, [.. parameters, ("$id", id)], ct).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Job {id} does not exist.");
        }
    }

    /// <summary>Reads the busy timeout that every store connection uses (tests).</summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The <c>busy_timeout</c> pragma of a store connection, in milliseconds.</returns>
    internal async Task<long> GetBusyTimeoutAsync(CancellationToken ct = default) =>
        Convert.ToInt64(await ExecuteScalarAsync("PRAGMA busy_timeout;", [], ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

    private async Task<object?> ExecuteScalarAsync(string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ExecuteNonQueryAsync(string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Several pipeline workers write at once: wait for another writer's lock instead of failing with SQLITE_BUSY.
            await ExecuteAsync(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", ct).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
