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

    private static readonly ForgeRules _rules = new()
    {
        Groups =
        [
            new GroupRule { Name = "Clutter", Match = [@"*\clutter\*"], Cap = 2048, Model = "m" },
            new GroupRule { Name = "Large", Match = ["*"], Cap = 4096, Model = "m" },
        ],
        Exclude = [],
    };

    private async Task<JobRecord> SeedDoneAsync(string path, string kind, string group, string? lookHash)
    {
        await _store.UpsertJobsAsync([Job(path) with { Kind = kind, Group = group, Target = 2048, Model = "m" }], Ct);
        var job = await SingleAsync(path);
        await _store.MarkDoneAsync(job.Id, 1.0, lookHash, Ct);
        return job;
    }

    private static string HashOf(ForgeRules rules, string group, Core.Textures.TextureKind kind) => LookResolver.Resolve(rules, group, null).GetStableHash(kind);

    [Fact]
    public async Task FindDoneLookChangesAsync_GroupLookChanged_ReturnsTheRow()
    {
        // Arrange
        var job = await SeedDoneAsync(@"textures\clutter\a.dds", "Diffuse", "Clutter", HashOf(_rules, "Clutter", Core.Textures.TextureKind.Diffuse));
        var edited = _rules with { Groups = [_rules.Groups[0] with { Look = new LookOverrides { Saturation = 1.3 } }, _rules.Groups[1]] };

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, edited, Ct);

        // Assert
        Assert.Equal([job.Id], ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_LookUnchanged_ReturnsNothing()
    {
        // Arrange
        await SeedDoneAsync(@"textures\clutter\a.dds", "Diffuse", "Clutter", HashOf(_rules, "Clutter", Core.Textures.TextureKind.Diffuse));

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, _rules, Ct);

        // Assert
        Assert.Empty(ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_ControlNotForTheKind_ReturnsNothing()
    {
        // Arrange: normal strength does not apply to a diffuse map.
        await SeedDoneAsync(@"textures\clutter\a.dds", "Diffuse", "Clutter", HashOf(_rules, "Clutter", Core.Textures.TextureKind.Diffuse));
        var edited = _rules with { Groups = [_rules.Groups[0] with { NormalStrength = 2.0 }, _rules.Groups[1]] };

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, edited, Ct);

        // Assert
        Assert.Empty(ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_RowMovesToGroupWithOtherLook_ReturnsTheRow()
    {
        // Arrange: the row was finished in Large; the edited rules send it to a new group with its own look.
        var job = await SeedDoneAsync(@"textures\armor\a.dds", "Diffuse", "Large", HashOf(_rules, "Large", Core.Textures.TextureKind.Diffuse));
        var edited = _rules with
        {
            Groups = [new GroupRule { Name = "Armor", Match = [@"*\armor\*"], Model = "m", Look = new LookOverrides { Contrast = 1.2 } }, .. _rules.Groups],
        };

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, edited, Ct);

        // Assert
        Assert.Equal([job.Id], ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_NoHashAndSameAsBefore_ReturnsNothing()
    {
        // Arrange: finished before look hashes were recorded.
        await SeedDoneAsync(@"textures\clutter\a_n.dds", "Normal", "Clutter", null);

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, _rules, Ct);

        // Assert
        Assert.Empty(ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_NoHashAndStrengthChanged_ReturnsTheRow()
    {
        // Arrange
        var job = await SeedDoneAsync(@"textures\clutter\a_n.dds", "Normal", "Clutter", null);
        var edited = _rules with { Groups = [_rules.Groups[0] with { NormalStrength = 2.0 }, _rules.Groups[1]] };

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, edited, Ct);

        // Assert
        Assert.Equal([job.Id], ids);
    }

    [Fact]
    public async Task FindDoneLookChangesAsync_TextureOverrideCoversTheChange_ReturnsNothing()
    {
        // Arrange: the texture sets its own saturation, so a group saturation change does not reach it.
        await _store.SetTextureLookAsync(@"textures\clutter\a.dds", new LookOverrides { Saturation = 0.8 }, Ct);
        var before = LookResolver.Resolve(_rules, "Clutter", new LookOverrides { Saturation = 0.8 }).GetStableHash(Core.Textures.TextureKind.Diffuse);
        await SeedDoneAsync(@"textures\clutter\a.dds", "Diffuse", "Clutter", before);
        var edited = _rules with { Groups = [_rules.Groups[0] with { Look = new LookOverrides { Saturation = 1.3 } }, _rules.Groups[1]] };

        // Act
        var ids = await RulesApplier.FindDoneLookChangesAsync(_store, _rules, edited, Ct);

        // Assert
        Assert.Empty(ids);
    }
}
