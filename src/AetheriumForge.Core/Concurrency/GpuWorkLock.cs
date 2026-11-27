using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// A machine-wide (per logon session) lock on GPU work: the named mutex <c>Local\AetheriumForge.GpuWork</c>. Pipeline runs (CLI and app)
/// and GPU calibration hold it, so two GPU jobs never run at once and calibration timings are not skewed.
/// </summary>
/// <remarks>
/// <para>
/// Mutex ownership is thread-affine, so each acquisition waits for, holds and releases the mutex on its own dedicated background thread;
/// the returned handle may be disposed from any thread. A holder that dies without releasing leaves an abandoned mutex, which the next
/// acquisition takes over (with a warning).
/// </para>
/// <para>
/// The holder writes a small owner file (<c>%LOCALAPPDATA%\AetheriumForge\gpu-work.owner</c>) so a process that finds the GPU busy can
/// say who holds it.
/// </para>
/// </remarks>
public sealed class GpuWorkLock
{
    /// <summary>The mutex name.</summary>
    public const string DefaultMutexName = @"Local\AetheriumForge.GpuWork";

    private static readonly TimeSpan ReleaseJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly string _ownerDescription;
    private readonly string _mutexName;
    private readonly string _ownerFilePath;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="GpuWorkLock"/> class with the default mutex and owner file.</summary>
    /// <param name="ownerDescription">What this process is, for example <c>Aetherium Forge</c> or <c>aetherium-forge forge</c>.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    public GpuWorkLock(string ownerDescription, ILogger<GpuWorkLock>? logger = null)
        : this(ownerDescription, DefaultMutexName, DefaultOwnerFilePath, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GpuWorkLock"/> class (tests use their own mutex and owner file).</summary>
    /// <param name="ownerDescription">What this process is.</param>
    /// <param name="mutexName">The mutex name.</param>
    /// <param name="ownerFilePath">The owner file.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    internal GpuWorkLock(string ownerDescription, string mutexName, string ownerFilePath, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerFilePath);

        _ownerDescription = ownerDescription;
        _mutexName = mutexName;
        _ownerFilePath = ownerFilePath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the default owner file, <c>%LOCALAPPDATA%\AetheriumForge\gpu-work.owner</c>.</summary>
    public static string DefaultOwnerFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "gpu-work.owner");

    /// <summary>Waits for the GPU lock.</summary>
    /// <param name="purpose">What the lock is for, phrased to follow "busy with", for example <c>a calibration</c> or <c>a forge run</c>.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock; dispose it to release.</returns>
    /// <exception cref="GpuBusyException">The lock was not free within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public Task<IDisposable> AcquireAsync(string purpose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        return Holder.Start(this, purpose, timeout, cancellationToken).Acquired;
    }

    /// <summary>Tries to take the GPU lock without waiting.</summary>
    /// <param name="purpose">What the lock is for.</param>
    /// <param name="handle">The held lock when taken; dispose it to release.</param>
    /// <param name="holder">Who holds the lock when it is busy, if that could be read.</param>
    /// <returns><see langword="true"/> when the lock was taken.</returns>
    public bool TryAcquire(string purpose, [NotNullWhen(true)] out IDisposable? handle, out LockOwnerInfo? holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var pending = Holder.Start(this, purpose, TimeSpan.Zero, CancellationToken.None);
        pending.WaitForOutcome();
        if (pending.Acquired.IsCompletedSuccessfully)
        {
            handle = pending.Acquired.Result;
            holder = null;
            return true;
        }

        handle = null;
        if (pending.Acquired.Exception?.InnerException is GpuBusyException busy)
        {
            holder = busy.Owner;
            return false;
        }

        // Not a busy lock: surface the real error (for example an access-denied mutex).
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(pending.Acquired.Exception!.InnerException!);
        holder = null;
        return false;
    }

    /// <summary>Reads who holds the GPU lock.</summary>
    /// <returns>The owner, or <see langword="null"/> when no live holder is recorded.</returns>
    public LockOwnerInfo? ReadOwner()
    {
        try
        {
            using var stream = new FileStream(_ownerFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var owner = LockOwnerInfo.TryParse(reader.ReadToEnd());
            return owner is not null && owner.IsProcessAlive() ? owner : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            // Diagnostics only: a busy GPU whose holder cannot be read is still reported as busy.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be non-negative (or infinite) and fit in Int32 milliseconds.");
        }
    }

    private void WriteOwnerFile(string purpose)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ownerFilePath)!);
            File.WriteAllText(_ownerFilePath, LockOwnerInfo.ForCurrentProcess(_ownerDescription, purpose).Serialize());
        }
        catch (IOException ex)
        {
            ConcurrencyLog.OwnerFileWriteFailed(_logger, ex, _ownerFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            ConcurrencyLog.OwnerFileWriteFailed(_logger, ex, _ownerFilePath);
        }
    }

    private void DeleteOwnerFile()
    {
        try
        {
            File.Delete(_ownerFilePath);
        }
        catch (IOException ex)
        {
            ConcurrencyLog.OwnerFileDeleteFailed(_logger, ex, _ownerFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            ConcurrencyLog.OwnerFileDeleteFailed(_logger, ex, _ownerFilePath);
        }
    }

    /// <summary>One acquisition: a dedicated thread that waits for, holds and releases the mutex.</summary>
    private sealed class Holder : IDisposable
    {
        private readonly GpuWorkLock _owner;
        private readonly string _purpose;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<IDisposable> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _decided = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private int _disposed;

        private Holder(GpuWorkLock owner, string purpose, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _owner = owner;
            _purpose = purpose;
            _timeout = timeout;
            _cancellationToken = cancellationToken;
            _thread = new Thread(Run) { IsBackground = true, Name = "AetheriumForge GPU work lock" };
        }

        public Task<IDisposable> Acquired => _acquired.Task;

        public static Holder Start(GpuWorkLock owner, string purpose, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var holder = new Holder(owner, purpose, timeout, cancellationToken);
            holder._thread.Start();
            return holder;
        }

        public void WaitForOutcome() => _decided.Wait();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _release.Set();
            if (Thread.CurrentThread != _thread && !_thread.Join(ReleaseJoinTimeout))
            {
                ConcurrencyLog.GpuReleaseSlow(_owner._logger, ReleaseJoinTimeout);
            }
        }

        private void Run()
        {
            Mutex? mutex = null;
            var held = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, _owner._mutexName);
                var abandoned = false;
                int signaled;
                try
                {
                    WaitHandle[] handles = _cancellationToken.CanBeCanceled ? [mutex, _cancellationToken.WaitHandle] : [mutex];
                    signaled = WaitHandle.WaitAny(handles, _timeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died without releasing: the mutex is now ours.
                    signaled = 0;
                    abandoned = true;
                }

                if (signaled == WaitHandle.WaitTimeout)
                {
                    _acquired.TrySetException(new GpuBusyException(_owner.ReadOwner()));
                    return;
                }

                if (signaled != 0)
                {
                    _acquired.TrySetCanceled(_cancellationToken);
                    return;
                }

                held = true;
                if (abandoned)
                {
                    ConcurrencyLog.GpuLockAbandoned(_owner._logger);
                }

                _owner.WriteOwnerFile(_purpose);
                ConcurrencyLog.GpuLockTaken(_owner._logger, _purpose);
                _acquired.TrySetResult(this);
                _decided.Set();
                _release.Wait();

                // Delete the owner file before releasing, so the next holder's file is never deleted by this one.
                _owner.DeleteOwnerFile();
                mutex.ReleaseMutex();
                held = false;
                ConcurrencyLog.GpuLockReleased(_owner._logger, _purpose);
            }
            catch (Exception ex)
            {
                // An exception must not escape this thread (it would end the process); report it to the waiter, or log it once held.
                if (held || _acquired.Task.IsCompleted)
                {
                    ConcurrencyLog.GpuReleaseFailed(_owner._logger, ex);
                }
                else
                {
                    _acquired.TrySetException(ex);
                }
            }
            finally
            {
                _decided.Set();
                mutex?.Dispose();
            }
        }
    }
}
