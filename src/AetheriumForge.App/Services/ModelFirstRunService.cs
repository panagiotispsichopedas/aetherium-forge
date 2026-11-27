using AetheriumForge.App.Pages.Models;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Services;

/// <summary>
/// Shows the Models first-run prompt at startup (IMPLEMENTATION_GUIDE §14, Models: "a first-run prompt") once the UI loop runs.
/// </summary>
public sealed partial class ModelFirstRunService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelFirstRunService"/> class.
    /// </summary>
    /// <param name="services">Resolves the Models view model when the prompt runs.</param>
    /// <param name="logger">Receives failures.</param>
    public ModelFirstRunService(IServiceProvider services, ILogger<ModelFirstRunService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Background priority: after the main window has been created and shown.
        Dispatcher.UIThread.Post(() => _ = RunAsync(), DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync()
    {
        try
        {
            await _services.GetRequiredService<ModelsViewModel>().RunFirstRunCheckAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed prompt must not take the app down; the Models page still offers every action.
            LogFailed(_logger, exception);
        }
    }

    [LoggerMessage(EventId = 420, Level = LogLevel.Error, Message = "The first-run model prompt failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
