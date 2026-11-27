using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Projects;

/// <summary>
/// One row of the <c>run</c> table.
/// </summary>
/// <param name="Id">Run id.</param>
/// <param name="Started">Start time.</param>
/// <param name="Ended">End time, or <see langword="null"/> while running or after a crash.</param>
/// <param name="Done">Jobs finished.</param>
/// <param name="Failed">Jobs failed.</param>
public sealed record RunRecord(long Id, DateTimeOffset? Started, DateTimeOffset? Ended, int Done, int Failed);

/// <summary>
/// Read-only queries on a project's <c>forge.db</c> that do not open a <c>JobStore</c> (which creates the schema and switches to WAL):
/// the latest runs for <c>status</c>, and project values needed before the store is opened (the game path, for the containment check).
/// </summary>
/// <remarks>
/// <c>JobStore</c> writes runs but has no query for them yet. Replace <see cref="ReadLatestRunsAsync"/> with a <c>JobStore.GetRunsAsync</c>
/// when Core gains one.
/// </remarks>
public static class ProjectDatabaseReader
{
    /// <summary>Reads one project value.</summary>
    /// <param name="databasePath">The project's <c>forge.db</c>.</param>
    /// <param name="key">The key.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The value, or <see langword="null"/> when the key (or the table) is missing.</returns>
    public static async Task<string?> ReadProjectValueAsync(string databasePath, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(key);

        var connection = new SqliteConnection(ReadOnly(databasePath));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT value FROM project WHERE key = $key AND EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'project');";
                command.Parameters.AddWithValue("$key", key);
                try
                {
                    return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string value ? value : null;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                {
                    // SQLITE_ERROR "no such table: project": a database that was never scanned has no values.
                    return null;
                }
            }
        }
    }

    /// <summary>Reads the latest runs, newest first.</summary>
    /// <param name="databasePath">The project's <c>forge.db</c>.</param>
    /// <param name="count">How many runs to return.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The runs.</returns>
    public static async Task<IReadOnlyList<RunRecord>> ReadLatestRunsAsync(string databasePath, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var connection = new SqliteConnection(ReadOnly(databasePath));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT id, started, ended, done, failed FROM run ORDER BY id DESC LIMIT $count;";
                command.Parameters.AddWithValue("$count", count);
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var runs = new List<RunRecord>();
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        runs.Add(new RunRecord(
                            reader.GetInt64(0),
                            reader.IsDBNull(1) ? null : Parse(reader.GetString(1)),
                            reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
                            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                            reader.IsDBNull(4) ? 0 : reader.GetInt32(4)));
                    }

                    return runs;
                }
            }
        }
    }

    private static string ReadOnly(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

    private static DateTimeOffset? Parse(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
