using AetheriumForge.App.Services;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// The "Stop and close" flow of the close prompt: requests a graceful stop and waits for the run to end. When the stop takes longer than
/// <see cref="ForceCloseDelay"/>, it offers "Force close" once.
/// </summary>
public sealed class RunStopFlow
{
    /// <summary>How long a graceful stop may take before "Force close" is offered.</summary>
    public static readonly TimeSpan DefaultForceCloseDelay = TimeSpan.FromSeconds(30);

    private readonly IForgeRunner _runner;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="RunStopFlow"/> class.</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="timeProvider">Clock for the force-close delay.</param>
    /// <param name="forceCloseDelay">How long to wait before offering "Force close".</param>
    public RunStopFlow(IForgeRunner runner, TimeProvider timeProvider, TimeSpan forceCloseDelay)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(forceCloseDelay, TimeSpan.Zero);
        _runner = runner;
        _timeProvider = timeProvider;
        ForceCloseDelay = forceCloseDelay;
    }

    /// <summary>Gets how long a graceful stop may take before "Force close" is offered.</summary>
    public TimeSpan ForceCloseDelay { get; }

    /// <summary>Requests a stop and completes when the run has ended.</summary>
    /// <param name="onForceCloseAvailable">Called once (on a thread-pool thread) when the stop is still running after <see cref="ForceCloseDelay"/>.</param>
    /// <param name="cancellationToken">Stops waiting (for example after "Force close").</param>
    /// <returns>A task that completes when the run has ended.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled first.</exception>
    public async Task StopAsync(Action onForceCloseAvailable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onForceCloseAvailable);

        _runner.RequestStop();
        using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idle = _runner.WaitForIdleAsync(cancellationToken);
        var delay = Task.Delay(ForceCloseDelay, _timeProvider, delayCancel.Token);
        var first = await Task.WhenAny(idle, delay).ConfigureAwait(false);
        if (first == delay && delay.IsCompletedSuccessfully && !idle.IsCompleted)
        {
            onForceCloseAvailable();
        }

        await delayCancel.CancelAsync().ConfigureAwait(false);
        await idle.ConfigureAwait(false);
    }
}
