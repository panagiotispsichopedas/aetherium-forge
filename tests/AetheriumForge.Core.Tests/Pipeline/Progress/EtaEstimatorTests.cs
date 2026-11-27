using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline.Progress;

public sealed class EtaEstimatorTests
{
    private const string Model = "x4plus";

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Constructor_SmoothingOutOfRange_Throws(double smoothing) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EtaEstimator(smoothing));

    [Fact]
    public void Record_SecondSample_AppliesMovingAverage()
    {
        var estimator = new EtaEstimator();
        Record(estimator, 10);

        Record(estimator, 20);

        Assert.Equal(12.0, Rate(estimator), 9);
    }

    [Fact]
    public void Record_SampleWithMegapixels_StoresSecondsPerMegapixel()
    {
        var estimator = new EtaEstimator();

        estimator.Record(Model, 4.0, TimeSpan.FromSeconds(20));

        Assert.Equal(5.0, Rate(estimator), 9);
    }

    [Fact]
    public void Record_FirstSampleAfterSeed_ReplacesSeed()
    {
        var estimator = new EtaEstimator();
        estimator.Seed(Model, 100);

        Record(estimator, 10);

        Assert.Equal(10.0, Rate(estimator), 9);
    }

    [Fact]
    public void Seed_AfterRealSample_IsIgnored()
    {
        var estimator = new EtaEstimator();
        Record(estimator, 10);

        estimator.Seed(Model, 100);

        Assert.Equal(10.0, Rate(estimator), 9);
    }

    [Theory]
    [InlineData(1000.0, 16.0)] // clamped to 40: 10 + 0.2 × 30
    [InlineData(0.001, 8.5)] // clamped to 2.5: 10 − 0.2 × 7.5
    public void Record_OutlierAfterMoreThanThreeSamples_IsClamped(double outlier, double expected)
    {
        var estimator = new EtaEstimator();
        for (var i = 0; i < 4; i++)
        {
            Record(estimator, 10);
        }

        Record(estimator, outlier);

        Assert.Equal(expected, Rate(estimator), 9);
    }

    [Fact]
    public void Record_OutlierWithThreeSamples_IsNotClamped()
    {
        var estimator = new EtaEstimator();
        for (var i = 0; i < 3; i++)
        {
            Record(estimator, 10);
        }

        Record(estimator, 1010);

        Assert.Equal(210.0, Rate(estimator), 9);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(1.0, -1.0)]
    public void Record_InvalidInput_IsIgnored(double megapixels, double seconds)
    {
        var estimator = new EtaEstimator();

        estimator.Record(Model, megapixels, TimeSpan.FromSeconds(seconds));

        Assert.False(estimator.TryGetRate(Model, out _));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Seed_InvalidRate_IsIgnored(double rate)
    {
        var estimator = new EtaEstimator();

        estimator.Seed(Model, rate);

        Assert.False(estimator.TryGetRate(Model, out _));
    }

    [Fact]
    public void Estimate_MultipleModels_SumsMegapixelsTimesRate()
    {
        var estimator = new EtaEstimator();
        estimator.Seed("a", 10);
        estimator.Seed("b", 1);

        var eta = estimator.Estimate([("a", 1.0), ("b", 4.0), ("a", 2.0)]);

        Assert.Equal(TimeSpan.FromSeconds(34), eta);
    }

    [Fact]
    public void Estimate_Parallelism_DividesTotal()
    {
        var estimator = new EtaEstimator();
        estimator.Seed(Model, 10);

        var eta = estimator.Estimate([(Model, 4.0)], parallelism: 2.0);

        Assert.Equal(TimeSpan.FromSeconds(20), eta);
    }

    [Fact]
    public void Estimate_ModelWithoutRate_ReturnsNull()
    {
        var estimator = new EtaEstimator();
        estimator.Seed(Model, 10);

        var eta = estimator.Estimate([(Model, 1.0), ("unknown-model", 1.0)]);

        Assert.Null(eta);
    }

    [Fact]
    public void Estimate_NothingRemaining_ReturnsZero()
    {
        var estimator = new EtaEstimator();

        var eta = estimator.Estimate([]);

        Assert.Equal(TimeSpan.Zero, eta);
    }

    [Fact]
    public void Estimate_InvalidMegapixelsAndParallelism_AreIgnored()
    {
        var estimator = new EtaEstimator();
        estimator.Seed(Model, 10);

        var eta = estimator.Estimate([(Model, 1.0), (Model, double.NaN), ("other", 0.0)], parallelism: 0);

        Assert.Equal(TimeSpan.FromSeconds(10), eta);
    }

    [Fact]
    public void Estimate_HugeTotal_SaturatesAtMaxValue()
    {
        var estimator = new EtaEstimator();
        estimator.Seed(Model, 1e300);

        var eta = estimator.Estimate([(Model, 1e100)]);

        Assert.Equal(TimeSpan.MaxValue, eta);
    }

    [Fact]
    public void Record_ConcurrentCalls_KeepsConsistentRate()
    {
        var estimator = new EtaEstimator();
        var options = new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken };

        Parallel.For(0, 10_000, options, i =>
        {
            Record(estimator, 10);
            estimator.Record($"model-{i % 8}", 1.0, TimeSpan.FromSeconds(10));
            _ = estimator.Estimate([(Model, 1.0)]);
        });

        Assert.Equal(10.0, Rate(estimator), 9);
        Assert.Equal(TimeSpan.FromSeconds(80), estimator.Estimate(Enumerable.Range(0, 8).Select(i => ($"model-{i}", 1.0))));
    }

    private static void Record(EtaEstimator estimator, double secondsPerMegapixel) =>
        estimator.Record(Model, 1.0, TimeSpan.FromSeconds(secondsPerMegapixel));

    private static double Rate(EtaEstimator estimator)
    {
        Assert.True(estimator.TryGetRate(Model, out var rate));
        return rate;
    }
}
