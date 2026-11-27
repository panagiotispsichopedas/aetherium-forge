namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Ctrl+C handling: the first press cancels <see cref="Token"/> (a graceful stop); the second lets the process terminate immediately.
/// </summary>
internal sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly TextWriter _error;
    private readonly Action _restoreConsole;
    private int _presses;

    /// <summary>Initializes a new instance of the <see cref="ConsoleCancellation"/> class and hooks <see cref="Console.CancelKeyPress"/>.</summary>
    /// <param name="linked">Also cancels when this token is cancelled.</param>
    /// <param name="error">Where the "stopping" notice goes.</param>
    public ConsoleCancellation(TextWriter error, CancellationToken linked)
        : this(error, ConsoleRestore.RestoreAll, hook: true, linked)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConsoleCancellation"/> class (tests: no console hook).</summary>
    /// <param name="error">Where the "stopping" notice goes.</param>
    /// <param name="restoreConsole">Undoes console changes before a forced exit.</param>
    /// <param name="hook">Subscribe to <see cref="Console.CancelKeyPress"/>.</param>
    /// <param name="linked">Also cancels when this token is cancelled.</param>
    internal ConsoleCancellation(TextWriter error, Action restoreConsole, bool hook, CancellationToken linked)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(restoreConsole);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(linked);
        _error = error;
        _restoreConsole = restoreConsole;
        if (hook)
        {
            Console.CancelKeyPress += OnCancelKeyPress;
        }
    }

    /// <summary>Gets the child processes (texconv) to kill on a forced exit; pass it to <see cref="Core.Imaging.TexconvOptions.ProcessTracker"/>.</summary>
    public Core.Imaging.ChildProcessTracker Processes { get; } = new();

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

    /// <summary>Handles one Ctrl+C press.</summary>
    /// <returns><see langword="true"/> to keep the process alive (first press); <see langword="false"/> to let it terminate.</returns>
    internal bool OnPress()
    {
        if (Interlocked.Increment(ref _presses) == 1)
        {
            _error.WriteLine();
            _error.WriteLine("Stopping after the current item... press Ctrl+C again to force exit.");
            _cts.Cancel();
            return true;
        }

        // Second press: kill texconv children (they would otherwise outlive us and lock the work folder), undo console changes, then
        // let Windows terminate the process. Work in progress returns to Queued on the next start (crash recovery).
        Processes.KillAll();
        try
        {
            _restoreConsole();
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Could not restore the console: {ex.Message}");
        }

        _error.WriteLine("Forced exit.");
        return false;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => e.Cancel = OnPress();
}
