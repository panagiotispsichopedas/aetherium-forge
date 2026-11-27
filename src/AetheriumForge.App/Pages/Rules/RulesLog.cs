using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// Source-generated log messages of the Rules page.
/// </summary>
internal static partial class RulesLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the GPU profiles; the Rules page shows no time estimate")]
    public static partial void GpuProfilesUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading the rules of {Project} failed")]
    public static partial void LoadFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied rules: {Updated} jobs updated, {Done} finished jobs changed, re-queued {Requeued}")]
    public static partial void Applied(ILogger logger, int updated, int done, bool requeued);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying the rules failed")]
    public static partial void ApplyFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The project's stored rules are invalid; using the scan rules")]
    public static partial void StoredRulesInvalid(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reloading the job rows failed")]
    public static partial void ReloadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rules page background work failed")]
    public static partial void BackgroundFailed(ILogger logger, Exception exception);
}
