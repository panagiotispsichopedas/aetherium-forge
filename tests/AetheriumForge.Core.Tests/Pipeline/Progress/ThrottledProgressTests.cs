using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline.Progress;

public sealed class ThrottledProgressTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly ManualTimeProvider _time = new();
    private readonly List<int> _received = [];

    [Fact]
    public void Report_FirstValue_ForwardsImmediately()
    {
        using var progress = Create();

        progress.Report(1);

        Assert.Equal([1], _received);
    }

    [Fact]
    public void Report_Burst_ForwardsOnlyLatestAfterInterval()
    {
        using var progress = Create();
        progress.Report(1);

        progress.Report(2);
        progress.Report(3);
        progress.Report(4);
        _time.Advance(TimeSpan.FromMilliseconds(99));
        var beforeInterval = _received.ToArray();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal([1], beforeInterval);
        Assert.Equal([1, 4], _received);
    }

    [Fact]
    public void Report_AfterQuietInterval_ForwardsImmediately()
    {
        using var progress = Create();
        progress.Report(1);
        _time.Advance(Interval);

        progress.Report(2);

        Assert.Equal([1, 2], _received);
    }

    [Fact]
    public void Report_ContinuousStream_ForwardsAtMostOncePerInterval()
    {
        using var progress = Create();

        for (var i = 0; i < 100; i++)
        {
            progress.Report(i);
            _time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal([0, 9, 19, 29, 39, 49, 59, 69, 79, 89, 99], _received);
    }

    [Fact]
    public void Flush_PendingValue_ForwardsNowAndOnlyOnce()
    {
        using var progress = Create();
        progress.Report(1);
        progress.Report(2);

        progress.Flush();
        var afterFlush = _received.ToArray();
        _time.Advance(Interval);

        Assert.Equal([1, 2], afterFlush);
        Assert.Equal([1, 2], _received);
    }

    [Fact]
    public void Dispose_PendingValue_FlushesAndIgnoresLaterReports()
    {
        var progress = Create();
        progress.Report(1);
        progress.Report(2);

        progress.Dispose();
        progress.Report(3);
        progress.Flush();
        progress.Dispose();
        _time.Advance(Interval);

        Assert.Equal([1, 2], _received);
    }

    [Fact]
    public void Constructor_ProgressTarget_ForwardsToIt()
    {
        var target = new ListProgress(_received);
        using var progress = new ThrottledProgress<int>(target, Interval, _time);

        progress.Report(7);

        Assert.Equal([7], _received);
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottledProgress<int>(_received.Add, TimeSpan.FromTicks(-1), _time));

    [Fact]
    public void Report_FromInsideTarget_DoesNotRecurse()
    {
        ThrottledProgress<int>? progress = null;
        var depth = 0;
        var maxDepth = 0;
        progress = new ThrottledProgress<int>(
            value =>
            {
                maxDepth = Math.Max(maxDepth, ++depth);
                _received.Add(value);
                if (value < 3)
                {
                    progress!.Report(value + 1);
                }

                depth--;
            },
            TimeSpan.Zero,
            _time);
        using var owned = progress;

        progress.Report(1);
        _time.Advance(TimeSpan.FromMilliseconds(10));

        Assert.Equal(1, maxDepth);
        Assert.Equal([1, 2, 3], _received);
    }

    [Fact]
    public void Flush_FromInsideTarget_ForwardsAfterTargetReturns()
    {
        ThrottledProgress<int>? progress = null;
        progress = new ThrottledProgress<int>(
            value =>
            {
                if (value == 1)
                {
                    progress!.Report(2);
                    progress.Flush();
                }

                _received.Add(value);
            },
            Interval,
            _time);
        using var owned = progress;

        progress.Report(1);

        Assert.Equal([1, 2], _received);
    }

    [Fact]
    public async Task Report_WhileTargetBusy_DoesNotBlockOrInvokeConcurrently()
    {
        var token = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var progress = new ThrottledProgress<int>(
            value =>
            {
                _received.Add(value);
                if (value == 1)
                {
                    entered.Set();
                    release.Wait(token);
                }
            },
            Interval,
            _time);
        var first = Task.Run(() => progress.Report(1), token);
        entered.Wait(token);

        _time.Advance(Interval);
        progress.Report(2);
        _time.Advance(Interval);
        var whileBusy = _received.ToArray();
        release.Set();
        await first;
        _time.Advance(Interval);

        Assert.Equal([1], whileBusy);
        Assert.Equal([1, 2], _received);
    }

    [Fact]
    public void Report_ConcurrentCallsWithRealTimer_NeverOverlapsAndDeliversLast()
    {
        var active = 0;
        var overlaps = 0;
        var last = 0;
        var progress = new ThrottledProgress<int>(
            value =>
            {
                if (Interlocked.Increment(ref active) > 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                Thread.SpinWait(200);
                last = value;
                Interlocked.Decrement(ref active);
            },
            TimeSpan.FromMilliseconds(1));
        var options = new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken };

        Parallel.For(0, 5000, options, i =>
        {
            progress.Report(i);
            if (i % 500 == 0)
            {
                progress.Flush();
            }
        });
        progress.Report(-1);
        progress.Dispose();

        Assert.Equal(0, overlaps);
        Assert.Equal(-1, last);
    }

    private ThrottledProgress<int> Create() => new(_received.Add, Interval, _time);

    private sealed class ListProgress(List<int> values) : IProgress<int>
    {
        public void Report(int value) => values.Add(value);
    }
}
