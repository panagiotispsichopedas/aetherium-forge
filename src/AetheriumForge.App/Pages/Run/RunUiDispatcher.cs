using Avalonia.Threading;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// Posts work to the UI thread. Abstracted so the Run view model can be tested without Avalonia.
/// </summary>
public interface IRunUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread; never blocks the caller.</summary>
    /// <param name="action">The work.</param>
    void Post(Action action);
}

/// <summary>
/// <see cref="IRunUiDispatcher"/> on <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class AvaloniaRunUiDispatcher : IRunUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }
}
