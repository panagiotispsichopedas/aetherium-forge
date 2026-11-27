using Avalonia.Threading;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// Runs work on the UI thread. Lets the Queue view model take timer callbacks and store events from any thread, and lets tests run
/// that work inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    /// <param name="action">The work.</param>
    void Post(Action action);
}

/// <summary>
/// <see cref="IUiDispatcher"/> on Avalonia's UI thread dispatcher.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher.UIThread.Post(action);
    }
}
