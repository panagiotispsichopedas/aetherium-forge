using AetheriumForge.App.Pages.Run;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Pipeline;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Shell;

/// <summary>
/// The main window. Closing it while a run is active asks to pause or stop first (<see cref="RunCloseDialog"/>).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IForgeRunner? _runner;
    private readonly ILogger _logger = NullLogger.Instance;
    private bool _closeConfirmed;

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class (designer).</summary>
    public MainWindow() => InitializeComponent();

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class.</summary>
    /// <param name="viewModel">The view model.</param>
    /// <param name="runner">The forge runner; closing during a run asks first.</param>
    /// <param name="logger">Logger.</param>
    public MainWindow(MainWindowViewModel viewModel, IForgeRunner runner, ILogger<MainWindow> logger)
        : this()
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        DataContext = viewModel;
        _runner = runner;
        _logger = logger;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _runner is null || _runner.State == PipelineState.Idle)
        {
            return;
        }

        e.Cancel = true;
        try
        {
            var killChildren = _runner is ForgeRunner forgeRunner ? forgeRunner.KillChildProcesses : (Func<int>?)null;
            var choice = await new RunCloseDialog(_runner, killChildren).ShowDialog<RunCloseChoice>(this);
            if (choice == RunCloseChoice.Cancel)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            // An async void handler must not crash the app: log and close anyway (crash recovery requeues interrupted jobs).
            LogClosePromptFailed(_logger, ex);
        }

        _closeConfirmed = true;
        Close();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The close prompt failed; closing the window")]
    private static partial void LogClosePromptFailed(ILogger logger, Exception exception);
}
