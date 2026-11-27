namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Ctrl+C handling: the first press cancels <see cref="Token"/> (a graceful stop); the second lets the process terminate immediately.
/// </summary>
internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly TextWriter _error;
    private int _presses;

    /// <summary>Initializes a new instance of the <see cref="ConsoleCancellation"/> class and hooks <see cref="Console.CancelKeyPress"/>.</summary>
    /// <param name="linked">Also cancels when this token is cancelled.</param>
    /// <param name="error">Where the "stopping" notice goes.</param>
    public ConsoleCancellation(TextWriter error, CancellationToken linked)
    {
        ArgumentNullException.ThrowIfNull(error);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(linked);
        _error = error;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>Gets the token cancelled by the first Ctrl+C (or by <see cref="Cancel"/>).</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Gets a value indicating whether a stop was requested.</summary>
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    /// <summary>Requests a graceful stop (the S key).</summary>
    public void Cancel() => _cts.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cts.Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (Interlocked.Increment(ref _presses) == 1)
        {
            e.Cancel = true;
            _error.WriteLine();
            _error.WriteLine("Stopping after the current item... press Ctrl+C again to force exit.");
            _cts.Cancel();
        }
        else
        {
            // Second press: let Windows terminate the process. Work in progress returns to Queued on the next start (crash recovery).
            e.Cancel = false;
        }
    }
}
