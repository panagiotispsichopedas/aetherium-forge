namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// <see cref="GpuWorkLock"/> could not be taken in time: another process (or another holder in this one) runs GPU work.
/// </summary>
public sealed class GpuBusyException : TimeoutException
{
    /// <summary>Initializes a new instance of the <see cref="GpuBusyException"/> class.</summary>
    public GpuBusyException()
        : base(FormatMessage(null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GpuBusyException"/> class.</summary>
    /// <param name="message">The message.</param>
    public GpuBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GpuBusyException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying error.</param>
    public GpuBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GpuBusyException"/> class.</summary>
    /// <param name="owner">Who holds the GPU, if known.</param>
    public GpuBusyException(LockOwnerInfo? owner)
        : base(FormatMessage(owner))
    {
        Owner = owner;
    }

    /// <summary>Gets who holds the GPU, or <see langword="null"/> when that could not be read.</summary>
    public LockOwnerInfo? Owner { get; }

    /// <summary>Formats the user-facing message, for example <c>The GPU is busy with a calibration in Aetherium Forge (PID 1234).</c></summary>
    /// <param name="owner">The holder, if known.</param>
    /// <returns>The message.</returns>
    public static string FormatMessage(LockOwnerInfo? owner) => owner is null
        ? "The GPU is busy with another Aetherium Forge job."
        : $"The GPU is busy with {owner.Purpose ?? "another job"} in {owner.Description} (PID {owner.ProcessId}).";
}
