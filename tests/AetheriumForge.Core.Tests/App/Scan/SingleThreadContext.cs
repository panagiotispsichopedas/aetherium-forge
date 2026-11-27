using System.Collections.Concurrent;

namespace AetheriumForge.Core.Tests.App.Scan;

/// <summary>
/// A stand-in UI thread: one dedicated thread with a <see cref="SynchronizationContext"/> that runs every continuation on it, like the
/// Avalonia dispatcher. Lets a test check that work leaves that thread.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    /// <summary>Runs <paramref name="action"/> on a new single-threaded context until its task completes.</summary>
    /// <param name="action">The work (for example executing a command).</param>
    /// <returns>The managed thread id of the context's thread.</returns>
    public static Task<int> RunAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new SingleThreadContext();
            SetSynchronizationContext(context);
            var threadId = Environment.CurrentManagedThreadId;
            Task task;
            try
            {
                task = action();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
                return;
            }

            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            if (task.Exception is { } error)
            {
                done.SetException(error.InnerExceptions);
            }
            else
            {
                done.SetResult(threadId);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return done.Task;
    }

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The awaited work has finished (or the context is disposed, a subclass); a late continuation has nothing left to update.
        }
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose() => _queue.Dispose();
}
