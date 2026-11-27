using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// Log messages of the concurrency locks.
/// </summary>
internal static partial class ConcurrencyLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not write the GPU lock owner file {OwnerFile}")]
    public static partial void OwnerFileWriteFailed(ILogger logger, Exception exception, string ownerFile);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not delete the GPU lock owner file {OwnerFile}")]
    public static partial void OwnerFileDeleteFailed(ILogger logger, Exception exception, string ownerFile);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The GPU lock thread did not release within {Timeout}")]
    public static partial void GpuReleaseSlow(ILogger logger, TimeSpan timeout);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The GPU work lock was abandoned by a process that exited without releasing it; taking it over")]
    public static partial void GpuLockAbandoned(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "GPU work lock taken for {Purpose}")]
    public static partial void GpuLockTaken(ILogger logger, string purpose);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "GPU work lock released after {Purpose}")]
    public static partial void GpuLockReleased(ILogger logger, string purpose);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Releasing the GPU work lock failed")]
    public static partial void GpuReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Asked the running instance (PID {Pid}) to come to the front; acknowledged: {Acknowledged}")]
    public static partial void SignaledPrimary(ILogger logger, int pid, bool acknowledged);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Could not reach the running instance on pipe {Pipe}")]
    public static partial void SignalFailed(ILogger logger, Exception exception, string pipe);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "The single-instance listener did not stop within {Timeout}")]
    public static partial void ListenerStopSlow(ILogger logger, TimeSpan timeout);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Ignored a malformed activation request")]
    public static partial void MalformedActivation(ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Another instance asked this one to come to the front ({Count} argument(s))")]
    public static partial void ActivationReceived(ILogger logger, int count);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "An activation request failed")]
    public static partial void ActivationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "An ActivationRequested handler failed")]
    public static partial void ActivationHandlerFailed(ILogger logger, Exception exception);
}
