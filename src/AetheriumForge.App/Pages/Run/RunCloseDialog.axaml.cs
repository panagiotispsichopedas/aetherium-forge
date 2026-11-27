using AetheriumForge.App.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

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

    /// <summary>The graceful stop took too long; texconv was killed; close now. Crash recovery requeues the interrupted jobs on the next start.</summary>
    ForceClose,
}

/// <summary>
/// Asked when the main window closes during a run: <b>Pause and close</b>, <b>Stop and close</b> (waits for a graceful stop with a spinner,
/// and offers <b>Force close</b> after <see cref="RunStopFlow.DefaultForceCloseDelay"/>) or <b>Cancel</b>. The result is a <see cref="RunCloseChoice"/>.
/// </summary>
public sealed partial class RunCloseDialog : Window
{
    private readonly IForgeRunner? _runner;
    private readonly Func<int>? _killChildProcesses;
    private readonly TaskCompletionSource _forced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="RunCloseDialog"/> class (designer).</summary>
    public RunCloseDialog() => InitializeComponent();

    /// <summary>Initializes a new instance of the <see cref="RunCloseDialog"/> class.</summary>
    /// <param name="runner">The runner to pause or stop.</param>
    /// <param name="killChildProcesses">Kills the texconv processes for "Force close", or <see langword="null"/>.</param>
    public RunCloseDialog(IForgeRunner runner, Func<int>? killChildProcesses)
        : this()
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _killChildProcesses = killChildProcesses;
        Closing += (_, e) =>
        {
            // While stopping, the dialog closes itself once the run has ended (or on Force close).
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
        var flow = new RunStopFlow(_runner, TimeProvider.System, RunStopFlow.DefaultForceCloseDelay);
        var stop = flow.StopAsync(() => Dispatcher.UIThread.Post(() => ForcePanel.IsVisible = true));
        if (await Task.WhenAny(stop, _forced.Task) == _forced.Task)
        {
            // Force close was chosen: OnForceClose already closed the dialog; the app exits without waiting for the stop.
            return;
        }

        await stop;

        _stopping = false;
        Close(RunCloseChoice.StopAndClose);
    }

    private void OnForceClose(object? sender, RoutedEventArgs e)
    {
        _killChildProcesses?.Invoke();
        _stopping = false;
        _forced.TrySetResult();
        Close(RunCloseChoice.ForceClose);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(RunCloseChoice.Cancel);
}
