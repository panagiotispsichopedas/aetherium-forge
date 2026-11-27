using AetheriumForge.App.Pages.Scan;
using AetheriumForge.Core.Discovery;

namespace AetheriumForge.Core.Tests.App.Scan;

public sealed class ScanSummaryTests
{
    [Fact]
    public void FromScan_Sample_CountsSkippedByReasonMostFirst()
    {
        var summary = ScanSummary.FromScan(ScanTestData.Sample(), "test");

        Assert.Equal([("TooSmall", 2), ("Cubemap", 1)], summary.SkippedByReason.Select(r => (r.Label, r.Count)));
    }

    [Fact]
    public void FromScan_Sample_GroupsQueuedByGroup()
    {
        var summary = ScanSummary.FromScan(ScanTestData.Sample(), "test");

        Assert.Equal([("architecture", 2), ("clutter", 1)], summary.ByGroup.Select(r => (r.Label, r.Count)));
    }

    [Fact]
    public void FromScan_Sample_GroupsQueuedByResolution()
    {
        var summary = ScanSummary.FromScan(ScanTestData.Sample(), "test");

        Assert.Equal([("1K", 1), ("2K", 2)], summary.ByResolution.Select(r => (r.Label, r.Count)));
    }

    [Fact]
    public void FromScan_Sample_EstimatesOutputLikeDriveAdvisor()
    {
        var summary = ScanSummary.FromScan(ScanTestData.Sample(), "test");

        Assert.Equal(DriveAdvisor.EstimateOutputBytes([2048, 2048, 1024]), summary.EstimatedOutputBytes);
    }

    [Theory]
    [InlineData(0, "unknown")]
    [InlineData(300, "512 px")]
    [InlineData(1024, "1K")]
    [InlineData(1500, "2K")]
    [InlineData(4096, "4K")]
    public void ResolutionBucket_Target_RoundsUpToPowerOfTwo(int target, string expected)
    {
        var bucket = ScanSummary.ResolutionBucket(target);

        Assert.Equal(expected, bucket);
    }
}
