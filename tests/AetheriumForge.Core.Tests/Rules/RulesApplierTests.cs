using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class RulesApplierTests
{
    private static readonly ForgeRules _rules = new()
    {
        Groups =
        [
            new GroupRule { Name = "Clutter", Match = [@"*\clutter\*"], Cap = 2048, MaxFactor = 4, Model = "model-a" },
            new GroupRule { Name = "Large", Match = ["*"], Cap = 4096, MaxFactor = 2, Model = "model-b" },
        ],
        MinSource = 128,
        Exclude = [@"textures\interface\"],
    };

    private static RulesPreview Preview(IEnumerable<JobRecord> jobs, ForgeRules rules, Func<string, double?>? timings = null) =>
        RulesApplier.Preview(jobs, rules, timings, TestContext.Current.CancellationToken);

    private static JobRecord Job(
        long id,
        string path,
        int width = 512,
        int height = 512,
        JobState state = JobState.Queued,
        string? group = null,
        int target = 0,
        string? model = null,
        string? error = null) =>
        new(id, path, "a.bsa", width, height, "BC1_UNORM", "Diffuse", group, target, model, state, 0, error, null, null);

    [Fact]
    public void Preview_MixedRows_CountsRowsPerGroup()
    {
        // Arrange
        JobRecord[] jobs =
        [
            Job(1, @"textures\clutter\a.dds"),
            Job(2, @"textures\clutter\b.dds"),
            Job(3, @"textures\architecture\c.dds"),
        ];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal([2, 1], preview.Groups.Select(g => g.Count));
    }

    [Fact]
    public void Preview_SkippedRows_CountsPerReason()
    {
        // Arrange
        JobRecord[] jobs =
        [
            Job(1, @"textures\interface\a.dds"),
            Job(2, @"textures\clutter\tiny.dds", 64, 64),
            Job(3, @"textures\clutter\big.dds", 2048, 2048),
        ];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal(
            new Dictionary<SkipReason, int> { [SkipReason.ExcludedFolder] = 1, [SkipReason.TooSmall] = 1, [SkipReason.AlreadyAtCap] = 1 },
            preview.SkippedByReason.OrderBy(kv => kv.Key).ToDictionary());
    }

    [Fact]
    public void Preview_UpscaledRow_EstimatesBc7SizeWithMips()
    {
        // Arrange: 512×256 clutter → ×4 → 2048×1024.
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", 512, 256)];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal((long)Math.Ceiling(2048 * 1024 * 4.0 / 3.0), preview.EstimatedBytes);
    }

    [Fact]
    public void Preview_TimingLookup_EstimatesSecondsFromSourceMegapixels()
    {
        // Arrange: 1000×1000 source = 1 MP at 2 s/MP.
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", 1000, 1000)];

        // Act
        var preview = Preview(jobs, _rules, model => model == "model-a" ? 2.0 : null);

        // Assert
        Assert.Equal(2.0, preview.EstimatedSeconds!.Value, 6);
    }

    [Fact]
    public void Preview_NoTimingLookup_HasNoTimeEstimate()
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds")];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Null(preview.EstimatedSeconds);
    }

    [Fact]
    public void Preview_DuplicateGroupNames_CountsEachGroupSeparately()
    {
        // Arrange
        var rules = _rules with
        {
            Groups = [_rules.Groups[0] with { Name = "Same" }, _rules.Groups[1] with { Name = "Same" }],
        };
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds"), Job(2, @"textures\x\b.dds"), Job(3, @"textures\x\c.dds")];

        // Act
        var preview = Preview(jobs, rules);

        // Assert
        Assert.Equal([1, 2], preview.Groups.Select(g => g.Count));
    }

    [Fact]
    public void Preview_QueuedRowNowExcluded_ChangesToSkippedWithReason()
    {
        // Arrange
        JobRecord[] jobs = [Job(7, @"textures\interface\a.dds", group: "Large", target: 1024, model: "model-b")];

        // Act
        var change = Assert.Single(Preview(jobs, _rules).Changes);

        // Assert
        Assert.Equal(new JobRuleChange(7, @"textures\interface\a.dds", JobState.Queued, JobState.Skipped, null, 0, null, "ExcludedFolder"), change);
    }

    [Fact]
    public void Preview_RuleSkippedRowNowMatches_ChangesToQueued()
    {
        // Arrange
        JobRecord[] jobs = [Job(3, @"textures\clutter\a.dds", state: JobState.Skipped, error: "NoMatchingGroup")];

        // Act
        var change = Assert.Single(Preview(jobs, _rules).Changes);

        // Assert
        Assert.Equal(new JobRuleChange(3, @"textures\clutter\a.dds", JobState.Skipped, JobState.Queued, "Clutter", 2048, "model-a", null), change);
    }

    [Fact]
    public void Preview_UnchangedQueuedRow_ProducesNoChange()
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", group: "Clutter", target: 2048, model: "model-a")];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Empty(preview.Changes);
    }

    [Theory]
    [InlineData(JobStore.ProvidedByModError)]
    [InlineData("Cubemap")]
    [InlineData("InvalidHeader")]
    public void Preview_SkippedForNonRuleReason_IsLeftAlone(string error)
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", state: JobState.Skipped, error: error)];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal((0, 1), (preview.Changes.Count, preview.NotRuleManaged));
    }

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Failed)]
    public void Preview_RunningOrFailedRow_ProducesNoChange(JobState state)
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\interface\a.dds", state: state, group: "Large", target: 1024, model: "model-b")];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal((0, 0), (preview.Changes.Count, preview.DoneChanges.Count));
    }

    [Fact]
    public void Preview_DoneRowWithNewTarget_IsListedOnlyInDoneChanges()
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", state: JobState.Done, group: "Clutter", target: 1024, model: "model-a")];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Equal((0, JobState.Queued), (preview.Changes.Count, Assert.Single(preview.DoneChanges).NewState));
    }

    [Fact]
    public void Preview_DoneRowWithSameOutput_ProducesNoChange()
    {
        // Arrange: only the group name differs; the output would be identical.
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", state: JobState.Done, group: "Old name", target: 2048, model: "model-a")];

        // Act
        var preview = Preview(jobs, _rules);

        // Assert
        Assert.Empty(preview.DoneChanges);
    }

    [Fact]
    public void Preview_DoneRow_IsNotPartOfTimeEstimate()
    {
        // Arrange
        JobRecord[] jobs = [Job(1, @"textures\clutter\a.dds", 1000, 1000, JobState.Done, "Clutter", 2048, "model-a")];

        // Act
        var preview = Preview(jobs, _rules, _ => 5.0);

        // Assert
        Assert.Equal(0.0, preview.EstimatedSeconds);
    }

    [Fact]
    public void Validate_ValidRules_ReturnsNull()
    {
        // Act
        var error = RulesApplier.Validate(ForgeRules.Default);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void Validate_NonPositiveCap_ReturnsMessage()
    {
        // Arrange
        var rules = _rules with { Groups = [_rules.Groups[0] with { Cap = 0 }] };

        // Act
        var error = RulesApplier.Validate(rules);

        // Assert
        Assert.Equal("Group 'Clutter': 'cap' must be positive.", error);
    }

    [Theory]
    [InlineData("TooSmall", true)]
    [InlineData("NoMatchingGroup", true)]
    [InlineData("Cubemap", false)]
    [InlineData("None", false)]
    [InlineData("3", false)]
    [InlineData(null, false)]
    public void IsRuleSkip_Error_ReturnsExpected(string? error, bool expected)
    {
        // Act
        var result = RulesApplier.IsRuleSkip(error);

        // Assert
        Assert.Equal(expected, result);
    }
}
