namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// The project folder is locked by another process (or another <see cref="ProjectLock"/> in this one).
/// </summary>
public sealed class ProjectLockedException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="ProjectLockedException"/> class.</summary>
    public ProjectLockedException()
        : base(FormatMessage(null))
    {
        LockPath = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ProjectLockedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ProjectLockedException(string message)
        : base(message)
    {
        LockPath = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ProjectLockedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying error.</param>
    public ProjectLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
        LockPath = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ProjectLockedException"/> class.</summary>
    /// <param name="lockPath">The lock file.</param>
    /// <param name="owner">Who holds it, if known.</param>
    public ProjectLockedException(string lockPath, LockOwnerInfo? owner)
        : base(FormatMessage(owner))
    {
        LockPath = lockPath;
        Owner = owner;
    }

    /// <summary>Gets the lock file.</summary>
    public string LockPath { get; }

    /// <summary>Gets who holds the lock, or <see langword="null"/> when that could not be read.</summary>
    public LockOwnerInfo? Owner { get; }

    /// <summary>Formats the user-facing message, for example <c>Project is open in Aetherium Forge (PID 1234). Close it there first.</c></summary>
    /// <param name="owner">The holder, if known.</param>
    /// <returns>The message.</returns>
    public static string FormatMessage(LockOwnerInfo? owner) => owner is null
        ? "Project is open in another Aetherium Forge process. Close it there first."
        : $"Project is open in {owner.Description} (PID {owner.ProcessId}). Close it there first.";
}
