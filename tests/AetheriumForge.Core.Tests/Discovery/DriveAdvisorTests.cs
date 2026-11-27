using AetheriumForge.Core.Discovery;

namespace AetheriumForge.Core.Tests.Discovery;

public sealed class DriveAdvisorTests
{
    [Fact]
    public void EstimateOutputBytes_SeveralTargets_SumsSquaresTimesOnePointThreeThree()
    {
        // Act: (2048² + 1024² + 512²) × 1.33 = 5_505_024 × 1.33 = 7_321_681.92
        var bytes = DriveAdvisor.EstimateOutputBytes([2048, 1024, 512]);

        // Assert
        Assert.Equal(7_321_681L, bytes);
    }

    [Fact]
    public void EstimateOutputBytes_NoTargets_ReturnsZero()
    {
        Assert.Equal(0L, DriveAdvisor.EstimateOutputBytes([]));
    }

    [Fact]
    public void EstimateOutputBytes_ManyLargeTargets_DoesNotOverflowInt32()
    {
        // Act: 10_000 × 8192² × 1.33
        var bytes = DriveAdvisor.EstimateOutputBytes(Enumerable.Repeat(8192, 10_000));

        // Assert
        Assert.Equal(892_547_891_200L, bytes);
    }

    [Fact]
    public void EstimateOutputBytes_NegativeDimension_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveAdvisor.EstimateOutputBytes([512, -1]));
    }

    [Fact]
    public void EstimateTempBytes_FourWorkersAt4K_ReturnsConcurrencyTimesSquareTimesTwelve()
    {
        // Act: 4 × 4096² × 4 × 3
        var bytes = DriveAdvisor.EstimateTempBytes(4, 4096);

        // Assert
        Assert.Equal(805_306_368L, bytes);
    }

    [Fact]
    public void EstimateTempBytes_NegativeConcurrency_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DriveAdvisor.EstimateTempBytes(-1, 1024));
    }

    [Fact]
    public void Rank_MixedDrives_FitsFirstThenNonSystemThenMostFree()
    {
        // Arrange
        (string Root, long Free, bool IsSystem)[] drives =
        [
            (@"C:\", 900, true),
            (@"D:\", 150, false),
            (@"E:\", 50, false),
            (@"F:\", 400, false),
            (@"G:\", 80, true),
        ];

        // Act
        var ranked = DriveAdvisor.Rank(drives, requiredBytes: 100);

        // Assert
        Assert.Equal([@"F:\", @"D:\", @"C:\", @"E:\", @"G:\"], ranked.Select(drive => drive.RootPath));
    }

    [Fact]
    public void Rank_OnlySystemDriveFits_PicksSystemDrive()
    {
        // Arrange
        (string Root, long Free, bool IsSystem)[] drives = [(@"C:\", 1_000, true), (@"D:\", 10, false)];

        // Act
        var best = DriveAdvisor.Rank(drives, requiredBytes: 500)[0];

        // Assert
        Assert.Equal(new DriveCandidate(@"C:\", 1_000, IsSystemDrive: true, Fits: true), best);
    }

    [Fact]
    public void Rank_NothingFits_FirstCandidateDoesNotFit()
    {
        // Arrange
        (string Root, long Free, bool IsSystem)[] drives = [(@"C:\", 10, true), (@"D:\", 20, false)];

        // Act
        var best = DriveAdvisor.Rank(drives, requiredBytes: 500)[0];

        // Assert
        Assert.Equal(new DriveCandidate(@"D:\", 20, IsSystemDrive: false, Fits: false), best);
    }

    [Fact]
    public void Rank_ExactFit_CountsAsFitting()
    {
        // Act
        var best = DriveAdvisor.Rank([(@"D:\", 100, false)], requiredBytes: 100)[0];

        // Assert
        Assert.True(best.Fits);
    }

    [Fact]
    public void GetFixedDrives_ThisMachine_ReturnsExactlyOneSystemDrive()
    {
        // Act
        var drives = DriveAdvisor.GetFixedDrives();

        // Assert
        Assert.Single(drives, drive => drive.IsSystem);
    }
}
