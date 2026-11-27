using System.Diagnostics;
using System.ComponentModel;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Keeps track of child processes (texconv) so a host can kill them when it is forced to exit. Without it, a process killed by a
/// second Ctrl+C would leave texconv running and holding files in the work folder.
/// </summary>
/// <remarks>Thread-safe. Processes remove themselves when the handle returned by <see cref="Track"/> is disposed.</remarks>
public sealed class ChildProcessTracker
{
    private readonly Lock _gate = new();
    private readonly HashSet<Process> _processes = [];

    /// <summary>Gets the number of tracked processes.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _processes.Count;
            }
        }
    }

    /// <summary>
    /// Tracks a started process until the returned handle is disposed.
    /// </summary>
    /// <param name="process">A started process.</param>
    /// <returns>A handle that stops tracking the process when disposed.</returns>
    public IDisposable Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            _processes.Add(process);
        }

        return new Registration(this, process);
    }

    /// <summary>
    /// Kills every tracked process and its children. Processes that already exited are ignored.
    /// </summary>
    /// <returns>The number of processes a kill was sent to.</returns>
    public int KillAll()
    {
        Process[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _processes];
        }

        var killed = 0;
        foreach (var process in snapshot)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited or was disposed between the snapshot and the kill: nothing left to stop.
            }
            catch (Win32Exception)
            {
                // Access denied or already terminating; the host is exiting, so there is nothing more it can do.
            }
        }

        return killed;
    }

    private void Untrack(Process process)
    {
        lock (_gate)
        {
            _processes.Remove(process);
        }
    }

    private sealed class Registration(ChildProcessTracker owner, Process process) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Untrack(process);
            }
        }
    }
}
