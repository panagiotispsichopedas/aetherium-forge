using AetheriumForge.Core.Jobs;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class JobStoreQueryTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _store = await JobStore.OpenAsync(Path.Combine(_folder, "forge.db"), Ct);
        await _store.UpsertJobsAsync(
            [
                Job(@"textures\clutter\bucket01.dds", JobState.Queued, "clutter", "diffuse", 512, 512, "model-b"),
                Job(@"textures\clutter\bucket01_n.dds", JobState.Done, "clutter", "normal", 1024, 1024, "model-a", error: null, seconds: 4.5),
                Job(@"textures\armor\iron\cuirass.dds", JobState.Failed, "armor", "diffuse", 256, 256, "model-c", error: "boom"),
                Job(@"textures\armor\iron\cuirass_n.dds", JobState.Skipped, "armor", "normal", 2048, 1024, null),
                Job(@"textures\effects\fx_50%_glow.dds", JobState.Running, null, "glow", 128, 128, "model-a"),
                Job(@"textures\effects\fxa_glow.dds", JobState.Queued, null, "glow", 64, 64, "model-a"),
            ],
            Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static JobRecord Job(string path, JobState state, string? group, string kind, int width, int height, string? model, string? error = null, double? seconds = null) =>
        new(0, path, "Skyrim - Textures0.bsa", width, height, "BC1_UNORM", kind, group, 2048, model, state, 0, error, seconds, null);

    private async Task<List<JobRecord>> QueryAsync(JobQuery query)
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in _store.QueryJobsAsync(query, Ct))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    private async Task<List<string>> PathsAsync(JobQuery query) => [.. (await QueryAsync(query)).Select(job => job.Path)];

    private async Task<long> IdOfAsync(string path) => (await QueryAsync(JobQuery.All)).Single(job => job.Path == path).Id;

    private async Task<JobRecord> JobAsync(string path) => (await QueryAsync(JobQuery.All)).Single(job => job.Path == path);

    [Fact]
    public async Task QueryJobsAsync_NoFilter_ReturnsEveryJobInIdOrder()
    {
        // Act
        var jobs = await QueryAsync(JobQuery.All);

        // Assert
        Assert.Equal(jobs.Select(job => job.Id).Order(), jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task QueryJobsAsync_StateSet_ReturnsOnlyThoseStates()
    {
        // Act
        var jobs = await QueryAsync(new JobQuery { States = new HashSet<JobState> { JobState.Queued, JobState.Failed } });

        // Assert
        Assert.Equal([JobState.Queued, JobState.Failed, JobState.Queued], jobs.Select(job => job.State));
    }

    [Fact]
    public async Task QueryJobsAsync_EmptyStateSet_ReturnsNothing()
    {
        // Act
        var jobs = await QueryAsync(new JobQuery { States = new HashSet<JobState>() });

        // Assert
        Assert.Empty(jobs);
    }

    [Fact]
    public async Task QueryJobsAsync_GroupAndKind_MatchesBoth()
    {
        // Act
        var paths = await PathsAsync(new JobQuery { Group = "clutter", Kind = "normal" });

        // Assert
        Assert.Equal([@"textures\clutter\bucket01_n.dds"], paths);
    }

    [Fact]
    public async Task QueryJobsAsync_TextWithForwardSlashAndCase_MatchesPath()
    {
        // Act
        var paths = await PathsAsync(new JobQuery { PathContains = "  Armor/Iron/ " });

        // Assert
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public async Task QueryJobsAsync_TextWithPercent_TreatsPercentAsLiteral()
    {
        // Act
        var paths = await PathsAsync(new JobQuery { PathContains = "50%" });

        // Assert
        Assert.Equal([@"textures\effects\fx_50%_glow.dds"], paths);
    }

    [Fact]
    public async Task QueryJobsAsync_TextWithUnderscore_TreatsUnderscoreAsLiteral()
    {
        // Act: unescaped, "fx_" would also match "fxa".
        var paths = await PathsAsync(new JobQuery { PathContains = "fx_" });

        // Assert
        Assert.Equal([@"textures\effects\fx_50%_glow.dds"], paths);
    }

    [Fact]
    public async Task QueryJobsAsync_TextWithSqlQuote_IsParameterised()
    {
        // Act
        var paths = await PathsAsync(new JobQuery { PathContains = "' OR 1=1 --" });

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public async Task QueryJobsAsync_SortByPathDescending_OrdersByPathDescending()
    {
        // Act
        var paths = await PathsAsync(new JobQuery { SortBy = JobSortColumn.Path, Descending = true });

        // Assert
        Assert.Equal(paths.OrderDescending(StringComparer.Ordinal), paths);
    }

    [Fact]
    public async Task QueryJobsAsync_SortBySource_OrdersByPixelCount()
    {
        // Act
        var jobs = await QueryAsync(new JobQuery { SortBy = JobSortColumn.Source });

        // Assert
        Assert.Equal([64, 128, 256, 512, 1024, 2048], jobs.Select(job => job.SourceWidth));
    }

    [Fact]
    public async Task QueryJobsAsync_SortByState_UsesLifecycleOrder()
    {
        // Act
        var jobs = await QueryAsync(new JobQuery { SortBy = JobSortColumn.State });

        // Assert
        Assert.Equal([JobState.Queued, JobState.Queued, JobState.Running, JobState.Done, JobState.Failed, JobState.Skipped], jobs.Select(job => job.State));
    }

    [Theory]
    [InlineData(JobSortColumn.Kind)]
    [InlineData(JobSortColumn.Group)]
    [InlineData(JobSortColumn.Target)]
    [InlineData(JobSortColumn.Model)]
    [InlineData(JobSortColumn.Attempts)]
    [InlineData(JobSortColumn.Seconds)]
    [InlineData(JobSortColumn.Error)]
    public async Task QueryJobsAsync_AnySortColumn_ReturnsEveryJob(JobSortColumn column)
    {
        // Act
        var jobs = await QueryAsync(new JobQuery { SortBy = column, Descending = true });

        // Assert
        Assert.Equal(6, jobs.Count);
    }

    [Fact]
    public async Task QueryJobsAsync_OffsetAndLimit_ReturnsPage()
    {
        // Arrange
        var all = await PathsAsync(new JobQuery { SortBy = JobSortColumn.Path });

        // Act
        var page = await PathsAsync(new JobQuery { SortBy = JobSortColumn.Path, Offset = 2, Limit = 3 });

        // Assert
        Assert.Equal(all.Skip(2).Take(3), page);
    }

    [Fact]
    public async Task QueryJobsAsync_OffsetWithoutLimit_ReturnsRest()
    {
        // Act
        var page = await PathsAsync(new JobQuery { Offset = 4 });

        // Assert
        Assert.Equal(2, page.Count);
    }

    [Fact]
    public async Task CountJobsAsync_Filters_CountsMatches()
    {
        // Act
        var count = await _store.CountJobsAsync(new JobQuery { Kind = "glow", States = new HashSet<JobState> { JobState.Queued } }, Ct);

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountByStateAsync_QueryWithStates_IgnoresStatesAndAppliesOtherFilters()
    {
        // Act
        var counts = await _store.CountByStateAsync(new JobQuery { Group = "armor", States = new HashSet<JobState> { JobState.Done } }, Ct);

        // Assert
        Assert.Equal((0, 1, 1), (counts[JobState.Done], counts[JobState.Failed], counts[JobState.Skipped]));
    }

    [Fact]
    public async Task SetStateAsync_IncludesRunningJob_LeavesRunningJobAlone()
    {
        // Arrange
        var running = await IdOfAsync(@"textures\effects\fx_50%_glow.dds");
        var queued = await IdOfAsync(@"textures\effects\fxa_glow.dds");

        // Act
        var changed = await _store.SetStateAsync([running, queued], JobState.Skipped, "user", Ct);

        // Assert
        Assert.Equal((1, JobState.Running), (changed, (await JobAsync(@"textures\effects\fx_50%_glow.dds")).State));
    }

    [Fact]
    public async Task SetStateAsync_FromStates_ChangesOnlyMatchingJobs()
    {
        // Arrange
        var ids = (await QueryAsync(JobQuery.All)).Select(job => job.Id).ToList();

        // Act
        var changed = await _store.SetStateAsync(ids, JobState.Queued, null, [JobState.Failed], Ct);

        // Assert
        Assert.Equal((1, 0), (changed, await _store.CountJobsAsync(new JobQuery { States = new HashSet<JobState> { JobState.Failed } }, Ct)));
    }

    [Fact]
    public async Task SetStateReturningIdsAsync_FromStates_ReturnsOnlyChangedIds()
    {
        // Arrange
        var ids = (await QueryAsync(JobQuery.All)).Select(job => job.Id).ToList();
        var doneId = await IdOfAsync(@"textures\clutter\bucket01_n.dds");

        // Act
        var changed = await _store.SetStateReturningIdsAsync(ids, JobState.Queued, null, [JobState.Done], Ct);

        // Assert
        Assert.Equal([doneId], changed);
    }

    [Fact]
    public async Task SetStateReturningIdsAsync_JobAlreadyMoved_ReturnsEmpty()
    {
        // Arrange: the job left Done before the caller's update ran.
        var doneId = await IdOfAsync(@"textures\clutter\bucket01_n.dds");
        await _store.SetStateAsync([doneId], JobState.Skipped, null, Ct);

        // Act
        var changed = await _store.SetStateReturningIdsAsync([doneId], JobState.Queued, null, [JobState.Done], Ct);

        // Assert
        Assert.Empty(changed);
    }

    [Fact]
    public async Task SetStateAsync_ToQueued_ClearsErrorTimeAndFinishKeepsAttempts()
    {
        // Arrange
        var id = await IdOfAsync(@"textures\clutter\bucket01_n.dds");
        await _store.MarkFailedAsync(id, "x", 2, Ct);
        await _store.MarkDoneAsync(id, 3.0, Ct);

        // Act
        await _store.SetStateAsync([id], JobState.Queued, null, Ct);

        // Assert
        var job = await JobAsync(@"textures\clutter\bucket01_n.dds");
        Assert.Equal((JobState.Queued, null, null, null, 2), (job.State, job.Error, job.Seconds, job.FinishedAt, job.Attempts));
    }

    [Fact]
    public async Task SetStateAsync_ToRunning_Throws()
    {
        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetStateAsync([1], JobState.Running, null, Ct));
    }

    [Fact]
    public async Task SetStateAsync_EmptyIds_ReturnsZero()
    {
        // Act
        var changed = await _store.SetStateAsync([], JobState.Skipped, null, Ct);

        // Assert
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task SetStateAsync_CancelledMidway_WritesNothing()
    {
        // Arrange
        var ids = (await QueryAsync(JobQuery.All)).Select(job => job.Id).ToList();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SetStateAsync(ids, JobState.Skipped, null, cts.Token));

        // Assert
        Assert.Equal(1, await _store.CountJobsAsync(new JobQuery { States = new HashSet<JobState> { JobState.Skipped } }, Ct));
    }

    [Fact]
    public async Task SetModelAsync_IncludesRunningJob_ChangesOthersOnly()
    {
        // Arrange
        var running = await IdOfAsync(@"textures\effects\fx_50%_glow.dds");
        var queued = await IdOfAsync(@"textures\effects\fxa_glow.dds");

        // Act
        var changed = await _store.SetModelAsync([running, queued], "model-z", Ct);

        // Assert
        Assert.Equal((1, "model-a", "model-z"), (changed, (await JobAsync(@"textures\effects\fx_50%_glow.dds")).Model, (await JobAsync(@"textures\effects\fxa_glow.dds")).Model));
    }

    [Fact]
    public async Task GetDistinctGroupsAsync_MixedGroups_ReturnsSortedNonNullGroups()
    {
        // Act
        var groups = await _store.GetDistinctGroupsAsync(Ct);

        // Assert
        Assert.Equal(["armor", "clutter"], groups);
    }

    [Fact]
    public async Task GetDistinctKindsAsync_MixedKinds_ReturnsSortedKinds()
    {
        // Act
        var kinds = await _store.GetDistinctKindsAsync(Ct);

        // Assert
        Assert.Equal(["diffuse", "glow", "normal"], kinds);
    }

    [Theory]
    [InlineData("a_b", "%a!_b%")]
    [InlineData("50%", "%50!%%")]
    [InlineData("x!y", "%x!!y%")]
    [InlineData("Tex/Clutter", @"%tex\clutter%")]
    public void ToContainsPattern_SpecialCharacters_AreEscaped(string text, string expected)
    {
        // Act
        var pattern = JobStore.ToContainsPattern(text);

        // Assert
        Assert.Equal(expected, pattern);
    }
}
