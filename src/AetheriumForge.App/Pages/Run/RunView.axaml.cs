using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// The Run page view. Keeps the log tail scrolled to the newest line.
/// </summary>
public sealed partial class RunView : UserControl
{
    private RunViewModel? _viewModel;

    /// <summary>Initializes a new instance of the <see cref="RunView"/> class.</summary>
    public RunView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as RunViewModel);
    }

    private void Attach(RunViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.LogLines.CollectionChanged -= OnLogChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.LogLines.CollectionChanged += OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel is not { LogLines.Count: > 0 } viewModel)
        {
            return;
        }

        // Scroll after layout so the new item has been realized.
        Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(viewModel.LogLines.Count - 1), DispatcherPriority.Background);
    }
}
