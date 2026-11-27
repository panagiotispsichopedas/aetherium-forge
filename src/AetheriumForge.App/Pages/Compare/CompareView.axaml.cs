using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// View for <see cref="CompareViewModel"/>. Pointer handling of the before/after viewport: drag the split line to move it, drag
/// elsewhere to pan both sides together, and use the mouse wheel to zoom.
/// </summary>
public sealed partial class CompareView : UserControl
{
    private const double SplitGrabDistance = 10;

    private DragMode _drag;
    private Point _lastPointer;

    /// <summary>Initializes a new instance of the <see cref="CompareView"/> class.</summary>
    public CompareView()
    {
        InitializeComponent();
        Viewport.PointerPressed += OnViewportPointerPressed;
        Viewport.PointerMoved += OnViewportPointerMoved;
        Viewport.PointerReleased += OnViewportPointerReleased;
        Viewport.PointerCaptureLost += (_, _) => _drag = DragMode.None;
        Viewport.PointerWheelChanged += OnViewportPointerWheelChanged;
        Viewport.SizeChanged += (_, args) =>
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.ViewportWidth = args.NewSize.Width;
                viewModel.ViewportHeight = args.NewSize.Height;
            }
        };
    }

    private enum DragMode
    {
        None,
        Split,
        Pan,
    }

    private CompareViewModel? ViewModel => DataContext as CompareViewModel;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (ViewModel is { } viewModel)
        {
            viewModel.ViewportWidth = Viewport.Bounds.Width;
            viewModel.ViewportHeight = Viewport.Bounds.Height;
            if (viewModel.Groups.Count == 0)
            {
                viewModel.RefreshCommand.Execute(null);
            }
        }
    }

    private void OnSliderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Slider { Tag: string name } && Enum.TryParse<LookControl>(name, out var control) && ViewModel is { } viewModel)
        {
            viewModel.Look.ResetControl(control);
            e.Handled = true;
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var onStage = e.GetPosition(Stage);
        _drag = Math.Abs(onStage.X - viewModel.SplitOffset) <= SplitGrabDistance ? DragMode.Split : DragMode.Pan;
        _lastPointer = e.GetPosition(Viewport);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ViewModel is not { } viewModel || _drag == DragMode.None)
        {
            return;
        }

        if (_drag == DragMode.Split)
        {
            viewModel.SetSplitFromOffset(e.GetPosition(Stage).X);
        }
        else
        {
            var position = e.GetPosition(Viewport);
            viewModel.PanBy(position.X - _lastPointer.X, position.Y - _lastPointer.Y);
            _lastPointer = position;
        }

        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _drag = DragMode.None;
        e.Pointer.Capture(null);
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            viewModel.ZoomIn();
        }
        else if (e.Delta.Y < 0)
        {
            viewModel.ZoomOut();
        }

        e.Handled = true;
    }
}
