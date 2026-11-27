using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Services;

/// <summary>
/// Reopens the last project when the app starts. Runs in the background so the window shows at once; <see cref="IProjectSession.Changed"/>
/// tells the pages when the project is open.
/// </summary>
/// <param name="session">The project session.</param>
/// <param name="logger">Logger.</param>
public sealed partial class ProjectSessionStartup(IScanProjectSession session, ILogger<ProjectSessionStartup> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _reopen;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reopen = Task.Run(ReopenAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_reopen is { } reopen)
        {
            await reopen.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The host may be disposed without StopAsync: cancel the reopen so the session's Dispose does not wait for it.
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task ReopenAsync()
    {
        try
        {
            if (await session.ReopenLastAsync(_stopping.Token).ConfigureAwait(false) && session.Current is { } project)
            {
                LogReopened(logger, project.Root);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            LogReopenCancelled(logger);
        }
#pragma warning disable CA1031 // A failed reopen must never take the app down; the user opens a project from the Scan page instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReopenFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Reopened the last project {Project}")]
    private static partial void LogReopened(ILogger logger, string project);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Reopening the last project was cancelled at shutdown")]
    private static partial void LogReopenCancelled(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Reopening the last project failed")]
    private static partial void LogReopenFailed(ILogger logger, Exception exception);
}
