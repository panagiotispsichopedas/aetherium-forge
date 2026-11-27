using AetheriumForge.Core.Jobs;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class JobStoreLookHashTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private string _databasePath = string.Empty;
    private JobStore? _store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "project.db");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task MarkDoneAsync_WithLookHash_StoresHash()
    {
        // Arrange
        JobStore store = await OpenAsync();
        long id = await InsertAsync(store, @"textures\a.dds");

        // Act
        await store.MarkDoneAsync(id, 1.5, "0123456789abcdef", Ct);

        // Assert
        Assert.Equal("0123456789abcdef", (await AllJobsAsync(store)).Single().LookHash);
    }

    [Fact]
    public async Task MarkDoneAsync_WithoutLookHash_ClearsPreviousHash()
    {
        // Arrange
        JobStore store = await OpenAsync();
        long id = await InsertAsync(store, @"textures\a.dds");
        await store.MarkDoneAsync(id, 1.5, "0123456789abcdef", Ct);

        // Act
        await store.MarkDoneAsync(id, 2.0, Ct);

        // Assert
        Assert.Null((await AllJobsAsync(store)).Single().LookHash);
    }

    [Fact]
    public async Task UpsertJobsAsync_DoneJobRescanned_KeepsLookHash()
    {
        // Arrange
        JobStore store = await OpenAsync();
        long id = await InsertAsync(store, @"textures\a.dds");
        await store.MarkDoneAsync(id, 1.5, "0123456789abcdef", Ct);

        // Act
        await store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);

        // Assert
        Assert.Equal("0123456789abcdef", (await AllJobsAsync(store)).Single().LookHash);
    }

    [Fact]
    public async Task FindDoneWithDifferentLookAsync_MixedJobs_ReturnsDoneJobsOfGroupWithOtherOrNoHash()
    {
        // Arrange
        JobStore store = await OpenAsync();
        long same = await InsertAsync(store, @"textures\same.dds");
        long other = await InsertAsync(store, @"textures\other.dds");
        long unknown = await InsertAsync(store, @"textures\unknown.dds");
        long otherGroup = await InsertAsync(store, @"textures\elsewhere.dds", group: "Large");
        await InsertAsync(store, @"textures\queued.dds");
        await store.MarkDoneAsync(same, 1, "aaaaaaaaaaaaaaaa", Ct);
        await store.MarkDoneAsync(other, 1, "bbbbbbbbbbbbbbbb", Ct);
        await store.MarkDoneAsync(unknown, 1, Ct);
        await store.MarkDoneAsync(otherGroup, 1, "bbbbbbbbbbbbbbbb", Ct);

        // Act
        IReadOnlyList<JobRecord> found = await store.FindDoneWithDifferentLookAsync("CLUTTER", "aaaaaaaaaaaaaaaa", Ct);

        // Assert
        Assert.Equal([@"textures\other.dds", @"textures\unknown.dds"], found.Select(j => j.Path));
    }

    [Fact]
    public async Task OpenAsync_DatabaseWithoutLookHashColumn_AddsColumnAndKeepsRows()
    {
        // Arrange: the M2/M3 schema, without look_hash, with one finished job.
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(Ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE project (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE job (
                  id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, archive TEXT NOT NULL,
                  src_w INT, src_h INT, format TEXT, kind TEXT, grp TEXT, target INT, model TEXT,
                  state TEXT NOT NULL, attempts INT DEFAULT 0, error TEXT, seconds REAL, finished_at TEXT);
                CREATE TABLE run (id INTEGER PRIMARY KEY, started TEXT, ended TEXT, done INT, failed INT);
                INSERT INTO job (path, archive, src_w, src_h, format, kind, grp, target, model, state)
                VALUES ('textures\old.dds', 'a.bsa', 512, 512, 'BC1_UNORM', 'diffuse', 'Clutter', 2048, 'm', 'Done');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        SqliteConnection.ClearAllPools();

        // Act
        JobStore store = await OpenAsync();
        IReadOnlyList<JobRecord> found = await store.FindDoneWithDifferentLookAsync("Clutter", "aaaaaaaaaaaaaaaa", Ct);

        // Assert
        Assert.Equal((@"textures\old.dds", (string?)null), (found.Single().Path, found.Single().LookHash));
    }

    [Fact]
    public async Task OpenAsync_CurrentDatabaseOpenedTwice_DoesNotFail()
    {
        // Arrange
        JobStore first = await JobStore.OpenAsync(_databasePath, Ct);
        await first.DisposeAsync();

        // Act
        JobStore store = await OpenAsync();
        long id = await InsertAsync(store, @"textures\a.dds");
        await store.MarkDoneAsync(id, 1, "cccccccccccccccc", Ct);

        // Assert
        Assert.Equal("cccccccccccccccc", (await AllJobsAsync(store)).Single().LookHash);
    }

    private static JobRecord Job(string path, string group = "Clutter") =>
        new(0, path, "a.bsa", 512, 512, "BC1_UNORM", "diffuse", group, 2048, "m", JobState.Queued, 0, null, null, null);

    private static async Task<long> InsertAsync(JobStore store, string path, string group = "Clutter")
    {
        await store.UpsertJobsAsync([Job(path, group)], Ct);
        return (await AllJobsAsync(store)).Single(j => j.Path == path).Id;
    }

    private static async Task<List<JobRecord>> AllJobsAsync(JobStore store)
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in store.GetJobsAsync(null, Ct))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    private async Task<JobStore> OpenAsync()
    {
        _store = await JobStore.OpenAsync(_databasePath, Ct);
        return _store;
    }
}
