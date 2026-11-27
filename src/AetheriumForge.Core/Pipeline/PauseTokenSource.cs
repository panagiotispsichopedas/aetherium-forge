namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// An async manual-reset gate used to pause long-running work (IMPLEMENTATION_GUIDE §11).
/// Work calls <see cref="PauseToken.WaitWhilePausedAsync"/> at safe points; it completes immediately while running
/// and waits while paused.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _paused;

    /// <summary>Gets a value indicating whether the source is paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused is not null;
            }
        }
    }

    /// <summary>Gets a token bound to this source.</summary>
    public PauseToken Token => new(this);

    /// <summary>Pauses. Has no effect when already paused.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Resumes. Has no effect when not paused.</summary>
    public void Resume()
    {
        TaskCompletionSource? toRelease;
        lock (_gate)
        {
            toRelease = _paused;
            _paused = null;
        }

        toRelease?.TrySetResult();
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate)
        {
            wait = _paused?.Task;
        }

        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// A cheap handle to a <see cref="PauseTokenSource"/>. The default value never pauses.
/// </summary>
public readonly struct PauseToken : IEquatable<PauseToken>
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource source) => _source = source;

    /// <summary>Gets a token that never pauses.</summary>
    public static PauseToken None => default;

    /// <summary>Gets a value indicating whether the source is currently paused.</summary>
    public bool IsPaused => _source?.IsPaused ?? false;

    /// <summary>
    /// Completes immediately when not paused; otherwise waits until resumed or cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when work may continue.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default) => _source?.WaitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public bool Equals(PauseToken other) => ReferenceEquals(_source, other._source);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PauseToken other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _source?.GetHashCode() ?? 0;

    /// <summary>Compares two tokens.</summary>
    /// <param name="left">Left token.</param>
    /// <param name="right">Right token.</param>
    /// <returns><see langword="true"/> when both use the same source.</returns>
    public static bool operator ==(PauseToken left, PauseToken right) => left.Equals(right);

    /// <summary>Compares two tokens.</summary>
    /// <param name="left">Left token.</param>
    /// <param name="right">Right token.</param>
    /// <returns><see langword="true"/> when the tokens use different sources.</returns>
    public static bool operator !=(PauseToken left, PauseToken right) => !left.Equals(right);
}
