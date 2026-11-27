using AetheriumForge.Core.Jobs;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class JobStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 11, 27, 10, 30, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private string _databasePath = string.Empty;
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "project.db");
        _store = await JobStore.OpenAsync(_databasePath, new FixedTimeProvider(_now), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static JobRecord Job(string path, JobState state = JobState.Queued, int target = 2048, int attempts = 0) =>
        new(0, path, "Skyrim - Textures0.bsa", 512, 512, "BC1_UNORM", "diffuse", "clutter", target, "4x-model", state, attempts, null, null, null);

    private async Task<List<JobRecord>> AllJobsAsync(JobState? state = null)
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in _store.GetJobsAsync(state, Ct))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    private async Task<JobRecord> SingleJobAsync(string path) => (await AllJobsAsync()).Single(job => job.Path == path);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task OpenAsync_NewDatabase_CreatesSchemaTablesAndIndex()
    {
        // Act
        var objects = await ScalarAsync("SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND name NOT LIKE 'sqlite_%' ORDER BY name);");

        // Assert
        Assert.Equal("job,job_state,project,run", objects);
    }

    [Fact]
    public async Task OpenAsync_NewDatabase_UsesWalJournal()
    {
        // Act
        var mode = await ScalarAsync("PRAGMA journal_mode;");

        // Assert
        Assert.Equal("wal", mode);
    }

    [Fact]
    public async Task OpenAsync_ExistingDatabase_KeepsData()
    {
        // Arrange
        await _store.SetProjectValueAsync("game", @"D:\Skyrim", Ct);
        await _store.DisposeAsync();

        // Act
        _store = await JobStore.OpenAsync(_databasePath, Ct);

        // Assert
        Assert.Equal(@"D:\Skyrim", await _store.GetProjectValueAsync("game", Ct));
    }

    [Fact]
    public async Task GetProjectValueAsync_MissingKey_ReturnsNull()
    {
        Assert.Null(await _store.GetProjectValueAsync("missing", Ct));
    }

    [Fact]
    public async Task SetProjectValueAsync_ExistingKey_OverwritesValue()
    {
        // Arrange
        await _store.SetProjectValueAsync("rules", "v1", Ct);

        // Act
        await _store.SetProjectValueAsync("rules", "v2", Ct);

        // Assert
        Assert.Equal("v2", await _store.GetProjectValueAsync("rules", Ct));
    }

    [Fact]
    public async Task UpsertJobsAsync_NewJobs_InsertsWithNormalisedPathsAndAllColumns()
    {
        // Act
        var affected = await _store.UpsertJobsAsync([Job("Textures/Clutter/Bucket01.DDS"), Job(@"textures\b.dds")], Ct);

        // Assert
        var job = await SingleJobAsync(@"textures\clutter\bucket01.dds");
        Assert.Equal(
            (2, "Skyrim - Textures0.bsa", 512, 512, "BC1_UNORM", "diffuse", "clutter", 2048, "4x-model", JobState.Queued, 0),
            (affected, job.Archive, job.SourceWidth, job.SourceHeight, job.Format, job.Kind, job.Group, job.Target, job.Model, job.State, job.Attempts));
    }

    [Fact]
    public async Task UpsertJobsAsync_SameJobsTwice_IsIdempotent()
    {
        // Arrange
        JobRecord[] jobs = [Job(@"textures\a.dds"), Job(@"textures\b.dds")];
        await _store.UpsertJobsAsync(jobs, Ct);

        // Act
        await _store.UpsertJobsAsync(jobs, Ct);

        // Assert
        Assert.Equal([@"textures\a.dds", @"textures\b.dds"], (await AllJobsAsync()).Select(job => job.Path));
    }

    [Fact]
    public async Task UpsertJobsAsync_ExistingDoneJob_KeepsStateAttemptsAndTiming()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;
        await _store.MarkFailedAsync(id, "out of memory", Ct);
        await _store.MarkDoneAsync(id, 12.5, Ct);

        // Act
        await _store.UpsertJobsAsync([Job(@"textures\a.dds", JobState.Queued, target: 4096)], Ct);

        // Assert
        var job = await SingleJobAsync(@"textures\a.dds");
        Assert.Equal((JobState.Done, 1, 12.5, _now, 4096), (job.State, job.Attempts, job.Seconds, job.FinishedAt, job.Target));
    }

    [Fact]
    public async Task UpsertJobsAsync_ExistingFailedJob_TakesIncomingStateAndKeepsAttempts()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;
        await _store.MarkFailedAsync(id, "boom", Ct);

        // Act
        await _store.UpsertJobsAsync([Job(@"textures\a.dds", JobState.Queued)], Ct);

        // Assert
        var job = await SingleJobAsync(@"textures\a.dds");
        Assert.Equal((id, JobState.Queued, 1, (string?)null), (job.Id, job.State, job.Attempts, job.Error));
    }

    [Fact]
    public async Task UpsertJobsAsync_Cancelled_WritesNothing()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        IEnumerable<JobRecord> Jobs()
        {
            yield return Job(@"textures\a.dds");
            cancellation.Cancel();
            yield return Job(@"textures\b.dds");
        }

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.UpsertJobsAsync(Jobs(), cancellation.Token));

        // Assert
        Assert.Empty(await AllJobsAsync());
    }

    [Fact]
    public async Task GetJobsAsync_StateFilter_ReturnsOnlyMatchingJobs()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds"), Job(@"textures\b.dds", JobState.Skipped), Job(@"textures\c.dds")], Ct);

        // Act
        var queued = await AllJobsAsync(JobState.Queued);

        // Assert
        Assert.Equal([@"textures\a.dds", @"textures\c.dds"], queued.Select(job => job.Path));
    }

    [Fact]
    public async Task MarkRunningAsync_QueuedJob_BecomesRunning()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        await _store.MarkRunningAsync(id, Ct);

        // Assert
        Assert.Equal(JobState.Running, (await SingleJobAsync(@"textures\a.dds")).State);
    }

    [Fact]
    public async Task MarkDoneAsync_FailedJob_ClearsErrorAndRecordsTiming()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;
        await _store.MarkFailedAsync(id, "boom", Ct);

        // Act
        await _store.MarkDoneAsync(id, 3.25, Ct);

        // Assert
        var job = await SingleJobAsync(@"textures\a.dds");
        Assert.Equal((JobState.Done, (string?)null, (double?)3.25, (DateTimeOffset?)_now), (job.State, job.Error, job.Seconds, job.FinishedAt));
    }

    [Fact]
    public async Task MarkFailedAsync_CalledTwice_IncrementsAttemptsAndKeepsLastError()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        await _store.MarkFailedAsync(id, "first", Ct);
        await _store.MarkFailedAsync(id, "second", Ct);

        // Assert
        var job = await SingleJobAsync(@"textures\a.dds");
        Assert.Equal((JobState.Failed, 2, "second"), (job.State, job.Attempts, job.Error));
    }

    [Fact]
    public async Task MarkRunningAsync_UnknownId_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.MarkRunningAsync(999, Ct));
    }

    [Fact]
    public async Task MarkRunningAsync_SkippedJob_ThrowsInvalidOperationException()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds", JobState.Skipped)], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        var act = () => _store.MarkRunningAsync(id, Ct);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Theory]
    [InlineData(JobState.Skipped)]
    [InlineData(JobState.Done)]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Failed)]
    public async Task TryMarkRunningAsync_NotQueued_ReturnsFalseAndKeepsState(JobState state)
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds", state)], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        var taken = await _store.TryMarkRunningAsync(id, Ct);

        // Assert
        Assert.Equal((false, state), (taken, (await SingleJobAsync(@"textures\a.dds")).State));
    }

    [Fact]
    public async Task TryMarkRunningAsync_Queued_ReturnsTrue()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        var taken = await _store.TryMarkRunningAsync(id, Ct);

        // Assert
        Assert.Equal((true, JobState.Running), (taken, (await SingleJobAsync(@"textures\a.dds")).State));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    public async Task MarkFailedAsync_WithAttempts_AddsAttemptsMade(int attempts, int expected)
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds")], Ct);
        var id = (await SingleJobAsync(@"textures\a.dds")).Id;

        // Act
        await _store.MarkFailedAsync(id, "boom", attempts, Ct);

        // Assert
        Assert.Equal(expected, (await SingleJobAsync(@"textures\a.dds")).Attempts);
    }

    [Fact]
    public async Task MarkFailedAsync_NegativeAttempts_ThrowsArgumentOutOfRange()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.MarkFailedAsync(1, "boom", -1, Ct));
    }

    [Fact]
    public async Task GetBusyTimeoutAsync_AnyConnection_UsesConfiguredTimeout()
    {
        // Act
        var timeout = await _store.GetBusyTimeoutAsync(Ct);

        // Assert
        Assert.Equal(JobStore.BusyTimeoutMilliseconds, timeout);
    }

    [Fact]
    public async Task RequeueInterruptedAsync_MixedStates_RequeuesOnlyRunning()
    {
        // Arrange
        await _store.UpsertJobsAsync(
        [
            Job(@"textures\queued.dds"),
            Job(@"textures\running.dds", JobState.Running),
            Job(@"textures\done.dds", JobState.Done),
            Job(@"textures\failed.dds", JobState.Failed),
            Job(@"textures\skipped.dds", JobState.Skipped),
        ], Ct);

        // Act
        var requeued = await _store.RequeueInterruptedAsync(Ct);

        // Assert
        var states = (await AllJobsAsync()).ToDictionary(job => job.Path, job => job.State);
        Assert.Equal(
            (1, JobState.Queued, JobState.Queued, JobState.Done, JobState.Failed, JobState.Skipped),
            (requeued, states[@"textures\queued.dds"], states[@"textures\running.dds"], states[@"textures\done.dds"], states[@"textures\failed.dds"], states[@"textures\skipped.dds"]));
    }

    [Fact]
    public async Task ApplyRescanAsync_JobsMissingFromGapSet_BecomeSkippedWithReason()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\keep.dds"), Job(@"textures\gone.dds"), Job(@"textures\done.dds", JobState.Done)], Ct);
        var gaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Textures/Keep.DDS" };

        // Act
        var skipped = await _store.ApplyRescanAsync(gaps, Ct);

        // Assert
        var jobs = (await AllJobsAsync()).ToDictionary(job => job.Path);
        Assert.Equal(
            (2, JobState.Queued, JobState.Skipped, JobStore.ProvidedByModError, JobState.Skipped),
            (skipped, jobs[@"textures\keep.dds"].State, jobs[@"textures\gone.dds"].State, jobs[@"textures\gone.dds"].Error, jobs[@"textures\done.dds"].State));
    }

    [Fact]
    public async Task ApplyRescanAsync_CalledTwice_OnlyCountsNewlySkippedJobs()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\gone.dds")], Ct);
        await _store.ApplyRescanAsync(new HashSet<string>(), Ct);

        // Act
        var skipped = await _store.ApplyRescanAsync(new HashSet<string>(), Ct);

        // Assert
        Assert.Equal(0, skipped);
    }

    [Fact]
    public async Task CountByStateAsync_MixedStates_CountsEveryState()
    {
        // Arrange
        await _store.UpsertJobsAsync([Job(@"textures\a.dds"), Job(@"textures\b.dds"), Job(@"textures\c.dds", JobState.Done)], Ct);

        // Act
        var counts = await _store.CountByStateAsync(Ct);

        // Assert
        Assert.Equal(
            new Dictionary<JobState, int> { [JobState.Queued] = 2, [JobState.Running] = 0, [JobState.Done] = 1, [JobState.Failed] = 0, [JobState.Skipped] = 0 },
            counts);
    }

    [Fact]
    public async Task StartRunAsync_ThenEndRunAsync_StoresRunRow()
    {
        // Arrange
        var runId = await _store.StartRunAsync(Ct);

        // Act
        await _store.EndRunAsync(runId, done: 40, failed: 2, Ct);

        // Assert
        var row = await ScalarAsync($"SELECT started || '|' || ended || '|' || done || '|' || failed FROM run WHERE id = {runId};");
        var stamp = _now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"{stamp}|{stamp}|40|2", row);
    }

    [Fact]
    public async Task StartRunAsync_CalledTwice_ReturnsIncreasingIds()
    {
        // Act
        var first = await _store.StartRunAsync(Ct);
        var second = await _store.StartRunAsync(Ct);

        // Assert
        Assert.True(second > first);
    }

    [Fact]
    public async Task EndRunAsync_UnknownRun_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.EndRunAsync(42, 0, 0, Ct));
    }

    [Fact]
    public async Task DisposeAsync_ThenUse_ThrowsObjectDisposedException()
    {
        // Arrange
        await _store.DisposeAsync();

        // Act / Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _store.GetProjectValueAsync("x", Ct));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
