using System.Diagnostics;

namespace AetheriumForge.Core.Tests.Concurrency;

/// <summary>
/// A child process (Windows PowerShell) that takes a lock with the same OS primitives as the production code, prints <c>READY</c>, and
/// holds it until stdin gets a line (graceful release) or the process is killed (crash).
/// </summary>
internal sealed class ChildHolder : IDisposable
{
    private readonly Process _process;

    private ChildHolder(Process process) => _process = process;

    public int ProcessId => _process.Id;

    /// <summary>Holds <paramref name="lockPath"/> like <c>ProjectLock</c> does (ReadWrite, share Read, delete on close).</summary>
    public static Task<ChildHolder> HoldProjectLockAsync(string lockPath) => StartAsync(
        $"$fs = New-Object IO.FileStream('{Escape(lockPath)}', 'OpenOrCreate', 'ReadWrite', 'Read', 1, 'DeleteOnClose'); "
        + "$b = [Text.Encoding]::UTF8.GetBytes(\"pid=$PID`nprocess=powershell`nowner=test holder`nstarted=2026-01-01T00:00:00.0000000+00:00`n\"); "
        + "$fs.Write($b, 0, $b.Length); $fs.Flush(); "
        + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; $fs.Dispose()");

    /// <summary>Owns the named mutex (and optionally writes a GPU owner file).</summary>
    public static Task<ChildHolder> HoldMutexAsync(string mutexName, string? ownerFile = null) => StartAsync(
        $"$m = New-Object Threading.Mutex($false, '{Escape(mutexName)}'); [void]$m.WaitOne(); "
        + (ownerFile is null ? string.Empty : $"[IO.File]::WriteAllText('{Escape(ownerFile)}', \"pid=$PID`nprocess=powershell`nowner=test holder`nstarted=2026-01-01T00:00:00.0000000+00:00`npurpose=a calibration`n\"); ")
        + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; $m.ReleaseMutex(); $m.Dispose()");

    /// <summary>Creates (without owning) the named mutex, as a running app instance does.</summary>
    public static Task<ChildHolder> CreateMutexAsync(string mutexName) => StartAsync(
        $"$m = New-Object Threading.Mutex($false, '{Escape(mutexName)}'); "
        + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; $m.Dispose()");

    /// <summary>Runs a script and returns its trimmed standard output.</summary>
    public static async Task<string> RunAsync(string script)
    {
        using var process = Start(script);
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return output.Trim();
    }

    public static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Releases gracefully and waits for the child to exit.</summary>
    public async Task ReleaseAsync()
    {
        await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await _process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kills the child (a crash) and waits for it to exit.</summary>
    public async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    private static async Task<ChildHolder> StartAsync(string script)
    {
        var process = Start(script);
        var holder = new ChildHolder(process);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (!string.Equals(line, "READY", StringComparison.Ordinal))
            {
                var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"The child did not get ready: '{line}' {error}");
            }

            return holder;
        }
        catch
        {
            holder.Dispose();
            throw;
        }
    }

    private static Process Start(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        return Process.Start(info) ?? throw new InvalidOperationException("powershell.exe did not start.");
    }
}
