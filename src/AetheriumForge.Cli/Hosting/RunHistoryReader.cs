using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// One row of the <c>run</c> table.
/// </summary>
/// <param name="Id">Run id.</param>
/// <param name="Started">Start time.</param>
/// <param name="Ended">End time, or <see langword="null"/> while running or after a crash.</param>
/// <param name="Done">Jobs finished.</param>
/// <param name="Failed">Jobs failed.</param>
internal sealed record RunRecord(long Id, DateTimeOffset? Started, DateTimeOffset? Ended, int Done, int Failed);

/// <summary>
/// Reads the most recent runs for <c>status</c>.
/// </summary>
/// <remarks>
/// <c>JobStore</c> writes runs but has no query for them yet, so this reads the table directly, read-only. Replace it with a
/// <c>JobStore.GetRunsAsync</c> when Core gains one.
/// </remarks>
internal static class RunHistoryReader
{
    /// <summary>Reads the latest runs, newest first.</summary>
    /// <param name="databasePath">The project's <c>forge.db</c>.</param>
    /// <param name="count">How many runs to return.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The runs.</returns>
    public static async Task<IReadOnlyList<RunRecord>> ReadLatestAsync(string databasePath, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var connection = new SqliteConnection(connectionString);
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

    private static DateTimeOffset? Parse(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
