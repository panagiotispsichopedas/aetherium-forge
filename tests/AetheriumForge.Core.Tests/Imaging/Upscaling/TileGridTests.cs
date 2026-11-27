using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

public sealed class TileGridTests
{
    [Fact]
    public void Split_LengthFitsInOneTile_ReturnsSingleSpanWithoutOverlap()
    {
        // Act
        var spans = TileGrid.Split(300, 384, 24);

        // Assert
        Assert.Equal([new TileSpan(0, 300, 0, 300)], spans);
    }

    [Fact]
    public void Split_NotMultipleOfTile_DistributesCoresEvenly()
    {
        // Act: 1000 px at 384 → 3 cores of 334, 333, 333.
        var spans = TileGrid.Split(1000, 384, 24);

        // Assert
        Assert.Equal([new TileSpan(0, 334, 0, 358), new TileSpan(334, 667, 310, 691), new TileSpan(667, 1000, 643, 1000)], spans);
    }

    [Fact]
    public void Split_SmallCores_LimitsOverlapToHalfCore()
    {
        // Act: 30 px at 10 → cores of 10; overlap 24 is limited to 5.
        var spans = TileGrid.Split(30, 10, 24);

        // Assert
        Assert.Equal([new TileSpan(0, 10, 0, 15), new TileSpan(10, 20, 5, 25), new TileSpan(20, 30, 15, 30)], spans);
    }

    [Theory]
    [InlineData(1, 384, 24)]
    [InlineData(385, 384, 24)]
    [InlineData(2048, 384, 24)]
    [InlineData(777, 64, 0)]
    [InlineData(13, 3, 2)]
    public void Split_AnyLength_CoresPartitionTheAxis(int length, int tileSize, int overlap)
    {
        // Act
        var spans = TileGrid.Split(length, tileSize, overlap);

        // Assert
        var covered = spans.SelectMany(span => Enumerable.Range(span.CoreStart, span.CoreLength)).ToArray();
        Assert.Equal(Enumerable.Range(0, length), covered);
    }

    [Theory]
    [InlineData(1000, 384, 24, 4)]
    [InlineData(130, 64, 8, 2)]
    [InlineData(30, 10, 24, 1)]
    public void FeatherWeights_AcrossAllTiles_SumToOneEverywhere(int length, int tileSize, int overlap, int scale)
    {
        // Arrange
        var spans = TileGrid.Split(length, tileSize, overlap);
        var sums = new float[length * scale];

        // Act
        foreach (var span in spans)
        {
            var weights = TileGrid.FeatherWeights(span, scale);
            for (var p = 0; p < weights.Length; p++)
            {
                sums[(span.InputStart * scale) + p] += weights[p];
            }
        }

        // Assert
        Assert.All(sums, sum => Assert.Equal(1f, sum, 1e-6f));
    }

    [Fact]
    public void FeatherWeights_SingleSpan_AreAllOne()
    {
        // Act
        var weights = TileGrid.FeatherWeights(new TileSpan(0, 5, 0, 5), 2);

        // Assert
        Assert.All(weights, weight => Assert.Equal(1f, weight));
    }
}
