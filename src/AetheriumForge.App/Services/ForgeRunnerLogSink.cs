using System.Collections.Concurrent;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace AetheriumForge.App.Services;

/// <summary>
/// A Serilog sink that feeds the Run page's live log tail: lines are formatted on the logging thread, queued, and delivered in batches
/// about every 100 ms through <see cref="LinesReceived"/>. At most <see cref="Capacity"/> lines are kept between batches.
/// </summary>
public sealed class ForgeRunnerLogSink : ILogEventSink, IDisposable
{
    /// <summary>The number of lines the log tail keeps.</summary>
    public const int Capacity = 500;

    /// <summary>The default interval between batches.</summary>
    public static readonly TimeSpan DefaultBatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly ITimer _timer;
    private int _flushing;

    /// <summary>Initializes a new instance of the <see cref="ForgeRunnerLogSink"/> class with the system clock.</summary>
    public ForgeRunnerLogSink()
        : this(TimeProvider.System, DefaultBatchInterval)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ForgeRunnerLogSink"/> class.</summary>
    /// <param name="timeProvider">Timer source.</param>
    /// <param name="batchInterval">The interval between batches.</param>
    public ForgeRunnerLogSink(TimeProvider timeProvider, TimeSpan batchInterval)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timer = timeProvider.CreateTimer(_ => Flush(), null, batchInterval, batchInterval);
    }

    /// <summary>Raised on a timer thread with the lines logged since the previous batch, oldest first.</summary>
    public event EventHandler<IReadOnlyList<string>>? LinesReceived;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        _pending.Enqueue(Format(logEvent));
        while (_pending.Count > Capacity && _pending.TryDequeue(out _))
        {
            // Older lines than the tail can show are dropped before they reach the UI.
        }
    }

    /// <summary>Delivers the queued lines now.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
        {
            return;
        }

        try
        {
            var batch = new List<string>();
            while (batch.Count < Capacity && _pending.TryDequeue(out var line))
            {
                batch.Add(line);
            }

            if (batch.Count > 0)
            {
                LinesReceived?.Invoke(this, batch);
            }
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    /// <summary>Formats one event as a log-tail line: time, level and message, plus the exception's first line.</summary>
    /// <param name="logEvent">The event.</param>
    /// <returns>The line.</returns>
    internal static string Format(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            _ => "FTL",
        };
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var line = string.Create(CultureInfo.InvariantCulture, $"{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {level} {message}");
        if (logEvent.Exception is { } ex)
        {
            var first = ex.Message.Split('\n', 2)[0].TrimEnd('\r');
            line = $"{line} — {ex.GetType().Name}: {first}";
        }

        return line;
    }
}
