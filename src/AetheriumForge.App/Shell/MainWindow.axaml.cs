using Avalonia.Controls;

namespace AetheriumForge.App.Shell;

/// <summary>
/// The main window. Closing it while a run is active is handled by the Run page work (ask to pause or stop).
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class (designer).</summary>
    public MainWindow() => InitializeComponent();

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class.</summary>
    /// <param name="viewModel">The view model.</param>
    public MainWindow(MainWindowViewModel viewModel)
        : this() => DataContext = viewModel;
}
