using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>View for <see cref="ScanViewModel"/>.</summary>
public sealed partial class ScanView : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="ScanView"/> class.</summary>
    public ScanView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // The view is created each time the page is shown; detection runs only the first time.
        if (DataContext is ScanViewModel { IsInitialized: false } viewModel)
        {
            viewModel.InitializeCommand.Execute(null);
        }
    }
}
