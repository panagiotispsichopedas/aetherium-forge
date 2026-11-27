using AetheriumForge.App.Pages.Run;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Pipeline;
using Avalonia.Controls;

namespace AetheriumForge.App.Shell;

/// <summary>
/// The main window. Closing it while a run is active asks to pause or stop first (<see cref="RunCloseDialog"/>).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IForgeRunner? _runner;
    private bool _closeConfirmed;

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class (designer).</summary>
    public MainWindow() => InitializeComponent();

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class.</summary>
    /// <param name="viewModel">The view model.</param>
    /// <param name="runner">The forge runner; closing during a run asks first.</param>
    public MainWindow(MainWindowViewModel viewModel, IForgeRunner runner)
        : this()
    {
        ArgumentNullException.ThrowIfNull(runner);
        DataContext = viewModel;
        _runner = runner;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _runner is null || _runner.State == PipelineState.Idle)
        {
            return;
        }

        e.Cancel = true;
        var choice = await new RunCloseDialog(_runner).ShowDialog<RunCloseChoice>(this);
        if (choice != RunCloseChoice.Cancel)
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
