using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class JobStoreApplyRuleChangesTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _store = await JobStore.OpenAsync(Path.Combine(_folder, "forge.db"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static JobRecord Job(string path) =>
        new(0, path, "a.bsa", 512, 512, "BC1_UNORM", "Diffuse", "Old", 1024, "old-model", JobState.Queued, 2, null, null, null);

    private async Task<JobRecord> SeedAsync(string path, JobState state = JobState.Queued)
    {
        await _store.UpsertJobsAsync([Job(path)], Ct);
        var job = await SingleAsync(path);
        if (state == JobState.Running)
        {
            await _store.MarkRunningAsync(job.Id, Ct);
        }
        else if (state == JobState.Done)
        {
            await _store.MarkDoneAsync(job.Id, 3.5, Ct);
        }

        return await SingleAsync(path);
    }

    private async Task<JobRecord> SingleAsync(string path)
    {
        await foreach (var job in _store.GetJobsAsync(null, Ct))
        {
            if (job.Path == path)
            {
                return job;
            }
        }

        throw new InvalidOperationException(path);
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_QueuedToSkipped_WritesNewColumns()
    {
        // Arrange
        var job = await SeedAsync(@"textures\a.dds");

        // Act
        await _store.ApplyRuleChangesAsync([new JobRuleChange(job.Id, job.Path, JobState.Queued, JobState.Skipped, null, 0, null, "ExcludedFolder")], Ct);

        // Assert
        var updated = await SingleAsync(job.Path);
        Assert.Equal((JobState.Skipped, (string?)null, 0, (string?)null, (string?)"ExcludedFolder", 2), (updated.State, updated.Group, updated.Target, updated.Model, updated.Error, updated.Attempts));
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_RunningRow_IsNotTouched()
    {
        // Arrange
        var job = await SeedAsync(@"textures\a.dds", JobState.Running);

        // Act
        var affected = await _store.ApplyRuleChangesAsync([new JobRuleChange(job.Id, job.Path, JobState.Running, JobState.Queued, "New", 2048, "m", null)], Ct);

        // Assert
        Assert.Equal((0, JobState.Running, "Old"), (affected, (await SingleAsync(job.Path)).State, (await SingleAsync(job.Path)).Group));
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_RowLeftExpectedState_IsNotTouched()
    {
        // Arrange: evaluated while Queued, then the pipeline took it.
        var job = await SeedAsync(@"textures\a.dds");
        await _store.MarkRunningAsync(job.Id, Ct);

        // Act
        var affected = await _store.ApplyRuleChangesAsync([new JobRuleChange(job.Id, job.Path, JobState.Queued, JobState.Skipped, null, 0, null, "TooSmall")], Ct);

        // Assert
        Assert.Equal((0, JobState.Running), (affected, (await SingleAsync(job.Path)).State));
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_DoneRequeued_ClearsSecondsAndFinishTime()
    {
        // Arrange
        var job = await SeedAsync(@"textures\a.dds", JobState.Done);

        // Act
        await _store.ApplyRuleChangesAsync([new JobRuleChange(job.Id, job.Path, JobState.Done, JobState.Queued, "New", 2048, "m", null)], Ct);

        // Assert
        var updated = await SingleAsync(job.Path);
        Assert.Equal((JobState.Queued, (double?)null, (DateTimeOffset?)null), (updated.State, updated.Seconds, updated.FinishedAt));
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_SeveralRows_ReturnsUpdatedCount()
    {
        // Arrange
        var a = await SeedAsync(@"textures\a.dds");
        var b = await SeedAsync(@"textures\b.dds");

        // Act
        var affected = await _store.ApplyRuleChangesAsync(
            [
                new JobRuleChange(a.Id, a.Path, JobState.Queued, JobState.Queued, "New", 2048, "m", null),
                new JobRuleChange(b.Id, b.Path, JobState.Queued, JobState.Skipped, null, 0, null, "TooSmall"),
            ],
            Ct);

        // Assert
        Assert.Equal(2, affected);
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_InvalidNewState_ThrowsAndWritesNothing()
    {
        // Arrange
        var a = await SeedAsync(@"textures\a.dds");
        var b = await SeedAsync(@"textures\b.dds");
        JobRuleChange[] changes =
        [
            new(a.Id, a.Path, JobState.Queued, JobState.Skipped, null, 0, null, "TooSmall"),
            new(b.Id, b.Path, JobState.Queued, JobState.Done, "New", 2048, "m", null),
        ];

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ApplyRuleChangesAsync(changes, Ct));

        // Assert
        Assert.Equal(JobState.Queued, (await SingleAsync(a.Path)).State);
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_WithRulesJson_StoresRules()
    {
        // Arrange
        var job = await SeedAsync(@"textures\a.dds");

        // Act
        await _store.ApplyRuleChangesAsync([new JobRuleChange(job.Id, job.Path, JobState.Queued, JobState.Skipped, null, 0, null, "TooSmall")], "{\"minSource\":1}", Ct);

        // Assert
        Assert.Equal("{\"minSource\":1}", await _store.GetProjectValueAsync(RulesApplier.RulesProjectKey, Ct));
    }

    [Fact]
    public async Task ApplyRuleChangesAsync_RowChangeFails_DoesNotStoreRules()
    {
        // Arrange
        var job = await SeedAsync(@"textures\a.dds");
        JobRuleChange[] changes = [new(job.Id, job.Path, JobState.Queued, JobState.Done, "New", 2048, "m", null)];

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ApplyRuleChangesAsync(changes, "{}", Ct));

        // Assert
        Assert.Null(await _store.GetProjectValueAsync(RulesApplier.RulesProjectKey, Ct));
    }
}
