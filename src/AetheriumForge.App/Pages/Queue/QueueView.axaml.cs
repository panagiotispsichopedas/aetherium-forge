using System.ComponentModel;
using AetheriumForge.Core.Jobs;
using Avalonia;
using Avalonia.Controls;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// The Queue page view. Code-behind only bridges what the <c>DataGrid</c> cannot bind: multi-selection, server-side sort requests
/// and the sort glyph in the column headers.
/// </summary>
public sealed partial class QueueView : UserControl
{
    private readonly Dictionary<DataGridColumn, string> _headers = [];
    private QueueViewModel? _viewModel;
    private bool _restoringSelection;
    private bool _subscribed;

    /// <summary>Initialises the view.</summary>
    public QueueView()
    {
        InitializeComponent();
        foreach (var column in JobsGrid.Columns)
        {
            _headers[column] = column.Header?.ToString() ?? string.Empty;
        }

        JobsGrid.Sorting += OnSorting;
        JobsGrid.SelectionChanged += OnSelectionChanged;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as QueueViewModel;
        if (IsInWindow())
        {
            AttachViewModel();
        }

        base.OnDataContextChanged(e);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachViewModel();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The view model is a singleton that outlives this view: drop its references to us so a detached view can be collected.
        DetachViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachViewModel()
    {
        if (_viewModel is null || _subscribed)
        {
            return;
        }

        _viewModel.SelectionRestoreRequested += OnSelectionRestoreRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribed = true;
        UpdateSortGlyphs();
    }

    private void DetachViewModel()
    {
        if (_viewModel is null || !_subscribed)
        {
            return;
        }

        _viewModel.SelectionRestoreRequested -= OnSelectionRestoreRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = false;
    }

    private bool IsInWindow() => TopLevel.GetTopLevel(this) is not null;

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        // The store sorts; never let the grid sort its in-memory view.
        e.Handled = true;
        if (_viewModel is not null && Enum.TryParse<JobSortColumn>(e.Column.SortMemberPath, out var column))
        {
            _viewModel.SortBy(column);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_restoringSelection)
        {
            _viewModel?.UpdateSelection(JobsGrid.SelectedItems.OfType<JobRowViewModel>());
        }
    }

    private void OnSelectionRestoreRequested(object? sender, IReadOnlyList<JobRowViewModel> rows)
    {
        _restoringSelection = true;
        try
        {
            JobsGrid.SelectedItems.Clear();
            foreach (var row in rows)
            {
                JobsGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _restoringSelection = false;
        }

        _viewModel?.UpdateSelection(JobsGrid.SelectedItems.OfType<JobRowViewModel>());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueViewModel.SortColumn) or nameof(QueueViewModel.SortDescending))
        {
            UpdateSortGlyphs();
        }
    }

    private void UpdateSortGlyphs()
    {
        if (_viewModel is null)
        {
            return;
        }

        var sortKey = _viewModel.SortColumn.ToString();
        foreach (var (column, header) in _headers)
        {
            column.Header = column.SortMemberPath == sortKey ? $"{header} {(_viewModel.SortDescending ? "▼" : "▲")}" : header;
        }
    }
}
