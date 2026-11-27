using AetheriumForge.Core.Concurrency;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The machine-wide GPU lock as the preview engine sees it: take it without waiting, or learn who holds it.
/// </summary>
public interface IPreviewGpuGate
{
    /// <summary>Tries to take the GPU lock without waiting.</summary>
    /// <param name="purpose">What it is for, phrased to follow "busy with" (for example <c>a Compare preview</c>).</param>
    /// <param name="handle">The held lock when taken; dispose it to release.</param>
    /// <param name="busyMessage">Who holds the lock when it is busy (a user-facing sentence).</param>
    /// <returns><see langword="true"/> when the lock was taken.</returns>
    bool TryAcquire(string purpose, out IDisposable? handle, out string? busyMessage);
}

/// <summary>
/// <see cref="IPreviewGpuGate"/> over Core's <see cref="GpuWorkLock"/> (one GPU job per machine: forge runs, calibration, previews).
/// </summary>
public sealed class GpuWorkLockGate : IPreviewGpuGate
{
    private readonly GpuWorkLock _lock;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuWorkLockGate"/> class.
    /// </summary>
    /// <param name="gpuLock">The machine-wide GPU lock.</param>
    public GpuWorkLockGate(GpuWorkLock gpuLock)
    {
        ArgumentNullException.ThrowIfNull(gpuLock);
        _lock = gpuLock;
    }

    /// <inheritdoc />
    public bool TryAcquire(string purpose, out IDisposable? handle, out string? busyMessage)
    {
        if (_lock.TryAcquire(purpose, out var held, out var holder))
        {
            handle = held;
            busyMessage = null;
            return true;
        }

        handle = null;
        busyMessage = GpuBusyException.FormatMessage(holder);
        return false;
    }
}
