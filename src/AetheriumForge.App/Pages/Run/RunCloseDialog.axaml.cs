using AetheriumForge.App.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// The user's answer to the close prompt.
/// </summary>
public enum RunCloseChoice
{
    /// <summary>Keep the window open.</summary>
    Cancel,

    /// <summary>The run was paused; close now.</summary>
    PauseAndClose,

    /// <summary>The run was stopped gracefully; close now.</summary>
    StopAndClose,
}

/// <summary>
/// Asked when the main window closes during a run: <b>Pause and close</b>, <b>Stop and close</b> (waits for a graceful stop with a spinner)
/// or <b>Cancel</b>. The dialog's result is a <see cref="RunCloseChoice"/>.
/// </summary>
public sealed partial class RunCloseDialog : Window
{
    private readonly IForgeRunner? _runner;
    private bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="RunCloseDialog"/> class (designer).</summary>
    public RunCloseDialog() => InitializeComponent();

    /// <summary>Initializes a new instance of the <see cref="RunCloseDialog"/> class.</summary>
    /// <param name="runner">The runner to pause or stop.</param>
    public RunCloseDialog(IForgeRunner runner)
        : this()
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        Closing += (_, e) =>
        {
            // While stopping, the dialog closes itself once the run has ended.
            if (_stopping)
            {
                e.Cancel = true;
            }
        };
    }

    private void OnPauseAndClose(object? sender, RoutedEventArgs e)
    {
        _runner?.Pause();
        Close(RunCloseChoice.PauseAndClose);
    }

    private async void OnStopAndClose(object? sender, RoutedEventArgs e)
    {
        if (_runner is null)
        {
            Close(RunCloseChoice.StopAndClose);
            return;
        }

        _stopping = true;
        ChoicePanel.IsVisible = false;
        StoppingPanel.IsVisible = true;
        _runner.RequestStop();
        await _runner.WaitForIdleAsync();
        _stopping = false;
        Close(RunCloseChoice.StopAndClose);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(RunCloseChoice.Cancel);
}
