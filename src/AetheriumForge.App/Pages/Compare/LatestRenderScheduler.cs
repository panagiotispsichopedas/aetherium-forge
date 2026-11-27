namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The outcome of one <see cref="LatestRenderScheduler{TRequest, TResult}.SubmitAsync"/> call.
/// </summary>
/// <typeparam name="TResult">The render result.</typeparam>
/// <param name="IsCurrent"><see langword="true"/> when this is the newest request's result; <see langword="false"/> (the default value) when a newer request superseded it.</param>
/// <param name="Result">The result when <paramref name="IsCurrent"/> is <see langword="true"/>; otherwise the default value.</param>
public readonly record struct RenderOutcome<TResult>(bool IsCurrent, TResult? Result);

/// <summary>
/// Runs renders one at a time in the background and keeps only the newest request: a request that is still waiting when a newer one
/// arrives is skipped, and a running render is cancelled (and its result dropped) when a newer request arrives. Slider drags therefore
/// render only the values the CPU can keep up with, and always end on the last value.
/// </summary>
/// <typeparam name="TRequest">The render request.</typeparam>
/// <typeparam name="TResult">The render result.</typeparam>
public sealed class LatestRenderScheduler<TRequest, TResult>
{
    private readonly Func<TRequest, CancellationToken, Task<TResult>> _render;
    private readonly Lock _gate = new();
    private Pending? _pending;
    private CancellationTokenSource? _running;
    private bool _workerActive;
    private long _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="LatestRenderScheduler{TRequest, TResult}"/> class.
    /// </summary>
    /// <param name="render">Renders one request; should observe the token between expensive steps.</param>
    public LatestRenderScheduler(Func<TRequest, CancellationToken, Task<TResult>> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        _render = render;
    }

    /// <summary>Gets the number of renders that ran to completion (current or not); for diagnostics and tests.</summary>
    public int CompletedRenders { get; private set; }

    /// <summary>
    /// Queues <paramref name="request"/> as the newest request, superseding any waiting or running one.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The outcome; superseded when a newer request arrived before this one finished.</returns>
    public Task<RenderOutcome<TResult>> SubmitAsync(TRequest request)
    {
        var completion = new TaskCompletionSource<RenderOutcome<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending? skipped;
        var startWorker = false;
        lock (_gate)
        {
            _generation++;
            skipped = _pending;
            _pending = new Pending(request, completion, _generation);
            _running?.Cancel();
            if (!_workerActive)
            {
                _workerActive = true;
                startWorker = true;
            }
        }

        skipped?.Completion.TrySetResult(default(RenderOutcome<TResult>));
        if (startWorker)
        {
            _ = Task.Run(RunWorkerAsync);
        }

        return completion.Task;
    }

    /// <summary>Supersedes any waiting or running request without queuing a new one.</summary>
    public void CancelAll()
    {
        Pending? skipped;
        lock (_gate)
        {
            _generation++;
            skipped = _pending;
            _pending = null;
            _running?.Cancel();
        }

        skipped?.Completion.TrySetResult(default(RenderOutcome<TResult>));
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            Pending next;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _workerActive = false;
                    _running = null;
                    return;
                }

                next = _pending;
                _pending = null;
                cancellation = new CancellationTokenSource();
                _running = cancellation;
            }

            using (cancellation)
            {
                try
                {
                    var result = await _render(next.Request, cancellation.Token).ConfigureAwait(false);
                    bool isCurrent;
                    lock (_gate)
                    {
                        CompletedRenders++;
                        isCurrent = next.Generation == _generation && !cancellation.IsCancellationRequested;
                    }

                    next.Completion.TrySetResult(isCurrent ? new RenderOutcome<TResult>(true, result) : default(RenderOutcome<TResult>));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    next.Completion.TrySetResult(default(RenderOutcome<TResult>));
                }
                catch (Exception ex)
                {
                    // Not swallowed: the submitter observes the failure through its task.
                    next.Completion.TrySetException(ex);
                }
                finally
                {
                    // Detach before the using block disposes it, so SubmitAsync never cancels a disposed source.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_running, cancellation))
                        {
                            _running = null;
                        }
                    }
                }
            }
        }
    }

    private sealed record Pending(TRequest Request, TaskCompletionSource<RenderOutcome<TResult>> Completion, long Generation);
}
