namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// An <see cref="IProgress{T}"/> that forwards at most once per interval; the last value wins. The first value is forwarded at once on the
/// reporting thread; values arriving inside the interval replace each other, and a trailing timer forwards the latest one.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe. The target is never invoked concurrently with itself, and never receives an older value after a newer one. It runs on the
/// thread that called <see cref="Report"/> or <see cref="Flush"/>, or on a timer thread; no synchronization context is captured, so a UI
/// target must marshal to its dispatcher itself, without blocking on it (a blocking marshal can deadlock with <see cref="Flush"/>).
/// </para>
/// <para>
/// <see cref="Report"/> does not wait for a slow target. Exceptions from the target are not caught: they reach the caller of
/// <see cref="Report"/> or <see cref="Flush"/>, or are unhandled when thrown on the timer thread, so the target must handle its own failures.
/// </para>
/// </remarks>
/// <typeparam name="T">The progress value type.</typeparam>
public sealed class ThrottledProgress<T> : IProgress<T>, IDisposable
{
    private readonly Lock _stateGate = new();
    private readonly Lock _deliveryGate = new();
    private readonly Action<T> _target;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;

    // Guarded by _stateGate.
    private T? _pending;
    private bool _hasPending;
    private long? _lastForwarded;
    private bool _timerArmed;
    private bool _disposed;

    // Guarded by _deliveryGate.
    private bool _delivering;
    private bool _flushRequested;

    /// <summary>Initializes a new instance of the <see cref="ThrottledProgress{T}"/> class that forwards to an <see cref="IProgress{T}"/>.</summary>
    /// <param name="target">Receiver of the throttled values.</param>
    /// <param name="interval">Minimum time between forwarded values.</param>
    /// <param name="timeProvider">Clock and timer source; <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    public ThrottledProgress(IProgress<T> target, TimeSpan interval, TimeProvider? timeProvider = null)
        : this(ToAction(target), interval, timeProvider)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ThrottledProgress{T}"/> class that forwards to a delegate.</summary>
    /// <param name="target">Receiver of the throttled values.</param>
    /// <param name="interval">Minimum time between forwarded values.</param>
    /// <param name="timeProvider">Clock and timer source; <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is negative.</exception>
    public ThrottledProgress(Action<T> target, TimeSpan interval, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        _target = target;
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reports a value. Ignored after <see cref="Dispose"/>.</summary>
    /// <param name="value">The value.</param>
    public void Report(T value)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = value;
            _hasPending = true;
            var wait = TimeUntilDue();
            if (wait > TimeSpan.Zero)
            {
                ArmTimer(wait);
                return;
            }
        }

        TryForwardDue();
    }

    /// <summary>
    /// Forwards the latest pending value now, waiting for a forward already in progress. When called from inside the target, the value is
    /// forwarded as soon as the target returns.
    /// </summary>
    public void Flush()
    {
        lock (_deliveryGate)
        {
            if (_delivering)
            {
                _flushRequested = true;
                return;
            }

            Forward(onlyIfDue: false);
        }
    }

    /// <summary>Flushes the pending value and stops the timer. Later reports are ignored.</summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            Flush();
        }
        finally
        {
            _timer.Dispose();
        }
    }

    private static Action<T> ToAction(IProgress<T> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Report;
    }

    private void OnTimer(object? state)
    {
        lock (_stateGate)
        {
            _timerArmed = false;
        }

        TryForwardDue();
    }

    /// <summary>Forwards the pending value if it is due, without blocking behind a forward already in progress.</summary>
    private void TryForwardDue()
    {
        if (!_deliveryGate.TryEnter())
        {
            RetryLater();
            return;
        }

        try
        {
            // The gate is reentrant: a target that reports from inside its own callback must not recurse.
            if (_delivering)
            {
                RetryLater();
                return;
            }

            Forward(onlyIfDue: true);
        }
        finally
        {
            _deliveryGate.Exit();
        }
    }

    /// <summary>Takes the latest pending value and invokes the target. Call while holding the delivery gate.</summary>
    private void Forward(bool onlyIfDue)
    {
        _delivering = true;
        try
        {
            do
            {
                _flushRequested = false;
                T value;
                lock (_stateGate)
                {
                    if (!_hasPending)
                    {
                        return;
                    }

                    var wait = onlyIfDue ? TimeUntilDue() : TimeSpan.Zero;
                    if (wait > TimeSpan.Zero)
                    {
                        ArmTimer(wait);
                        return;
                    }

                    value = _pending!;
                    _pending = default;
                    _hasPending = false;
                    _lastForwarded = _timeProvider.GetTimestamp();
                }

                _target(value);
                onlyIfDue = false;
            }
            while (_flushRequested);
        }
        finally
        {
            _delivering = false;
        }
    }

    private void RetryLater()
    {
        lock (_stateGate)
        {
            if (_hasPending && !_disposed)
            {
                ArmTimer(_interval);
            }
        }
    }

    /// <summary>Time left before another value may be forwarded. Call under the state gate.</summary>
    private TimeSpan TimeUntilDue() =>
        _lastForwarded is { } last ? _interval - _timeProvider.GetElapsedTime(last) : TimeSpan.Zero;

    /// <summary>Schedules the trailing forward unless one is already scheduled. Call under the state gate.</summary>
    private void ArmTimer(TimeSpan delay)
    {
        if (_timerArmed || _disposed)
        {
            return;
        }

        _timerArmed = true;

        // Timers have millisecond resolution; round up so the timer does not fire before the value is due.
        var due = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }
}
