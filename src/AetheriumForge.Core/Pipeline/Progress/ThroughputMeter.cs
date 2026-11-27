namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Thread-safe throughput of completed items over a sliding time window. Until a full window has passed since construction (or
/// <see cref="Reset"/>), rates are taken over the time that has passed, but never over less than one second.
/// </summary>
public sealed class ThroughputMeter
{
    private static readonly TimeSpan _minimumSpan = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Queue<(long Timestamp, double Megapixels)> _items = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;
    private long _start;

    /// <summary>Initializes a new instance of the <see cref="ThroughputMeter"/> class.</summary>
    /// <param name="window">Window length; <see langword="null"/> means 60 seconds.</param>
    /// <param name="timeProvider">Clock; <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not positive.</exception>
    public ThroughputMeter(TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        _window = window ?? TimeSpan.FromSeconds(60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_window, TimeSpan.Zero, nameof(window));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _start = _timeProvider.GetTimestamp();
    }

    /// <summary>Gets the source megapixels completed per second within the window.</summary>
    public double MegapixelsPerSecond
    {
        get
        {
            lock (_gate)
            {
                var span = Trim();
                var total = 0.0;
                foreach (var item in _items)
                {
                    total += item.Megapixels;
                }

                return total / span.TotalSeconds;
            }
        }
    }

    /// <summary>Gets the items completed per minute within the window.</summary>
    public double ItemsPerMinute
    {
        get
        {
            lock (_gate)
            {
                var span = Trim();
                return _items.Count / span.TotalMinutes;
            }
        }
    }

    /// <summary>Records one completed item. A negative or non-finite size is ignored; zero counts the item only.</summary>
    /// <param name="sourceMegapixels">Source width × height / 1,000,000.</param>
    public void Record(double sourceMegapixels)
    {
        if (!(sourceMegapixels >= 0 && double.IsFinite(sourceMegapixels)))
        {
            return;
        }

        lock (_gate)
        {
            Trim();
            _items.Enqueue((_timeProvider.GetTimestamp(), sourceMegapixels));
        }
    }

    /// <summary>Forgets all items and restarts the window, for example after a pause so idle time does not count.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _items.Clear();
            _start = _timeProvider.GetTimestamp();
        }
    }

    /// <summary>Drops expired items and returns the time span the remaining ones are measured over. Call under the lock.</summary>
    private TimeSpan Trim()
    {
        var now = _timeProvider.GetTimestamp();
        while (_items.TryPeek(out var oldest) && _timeProvider.GetElapsedTime(oldest.Timestamp, now) >= _window)
        {
            _items.Dequeue();
        }

        var age = _timeProvider.GetElapsedTime(_start, now);
        var span = age < _minimumSpan ? _minimumSpan : age;
        return span < _window ? span : _window;
    }
}
