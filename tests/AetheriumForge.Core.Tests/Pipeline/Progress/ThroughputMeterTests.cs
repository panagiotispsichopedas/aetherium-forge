using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline.Progress;

public sealed class ThroughputMeterTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void MegapixelsPerSecond_NoItems_ReturnsZero()
    {
        var meter = new ThroughputMeter(timeProvider: _time);

        Assert.Equal(0.0, meter.MegapixelsPerSecond);
    }

    [Fact]
    public void MegapixelsPerSecond_BeforeFullWindow_UsesElapsedTime()
    {
        var meter = new ThroughputMeter(timeProvider: _time);
        _time.Advance(TimeSpan.FromSeconds(10));
        meter.Record(4);
        meter.Record(1);

        Assert.Equal(0.5, meter.MegapixelsPerSecond, 9);
        Assert.Equal(12.0, meter.ItemsPerMinute, 9);
    }

    [Fact]
    public void MegapixelsPerSecond_ImmediatelyAfterStart_UsesAtLeastOneSecond()
    {
        var meter = new ThroughputMeter(timeProvider: _time);
        meter.Record(4);

        Assert.Equal(4.0, meter.MegapixelsPerSecond, 9);
    }

    [Fact]
    public void MegapixelsPerSecond_ItemsOlderThanDefaultWindow_Expire()
    {
        var meter = new ThroughputMeter(timeProvider: _time);
        meter.Record(100);
        _time.Advance(TimeSpan.FromSeconds(30));
        meter.Record(6);

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0.1, meter.MegapixelsPerSecond, 9);
        Assert.Equal(1.0, meter.ItemsPerMinute, 9);
    }

    [Fact]
    public void ItemsPerMinute_CustomWindowFullyExpired_ReturnsZero()
    {
        var meter = new ThroughputMeter(TimeSpan.FromSeconds(5), _time);
        meter.Record(1);

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0.0, meter.ItemsPerMinute);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Record_InvalidSize_IsIgnored(double megapixels)
    {
        var meter = new ThroughputMeter(timeProvider: _time);

        meter.Record(megapixels);

        Assert.Equal(0.0, meter.ItemsPerMinute);
    }

    [Fact]
    public void Reset_AfterIdleTime_RestartsWindow()
    {
        var meter = new ThroughputMeter(timeProvider: _time);
        meter.Record(50);
        _time.Advance(TimeSpan.FromSeconds(40));

        meter.Reset();
        _time.Advance(TimeSpan.FromSeconds(2));
        meter.Record(4);

        Assert.Equal(2.0, meter.MegapixelsPerSecond, 9);
    }

    [Fact]
    public void Constructor_NonPositiveWindow_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThroughputMeter(TimeSpan.Zero, _time));

    [Fact]
    public void Record_ConcurrentCalls_CountsEveryItem()
    {
        var meter = new ThroughputMeter(timeProvider: _time);
        _time.Advance(TimeSpan.FromSeconds(60));
        var options = new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken };

        Parallel.For(0, 1000, options, _ => meter.Record(0.5));

        Assert.Equal(1000.0, meter.ItemsPerMinute, 9);
    }
}
