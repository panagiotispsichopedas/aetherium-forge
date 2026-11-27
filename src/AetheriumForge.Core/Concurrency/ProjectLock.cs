using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// An exclusive, cross-process lock on a project folder: <c>&lt;project&gt;\.aetherium.lock</c> kept open for the lock's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The holder opens the file for read/write and shares only <see cref="FileShare.Read"/>, so no other process can open it for writing
/// (and so take the lock), while a process that finds the lock taken can still read who holds it. The file is opened with
/// <see cref="FileOptions.DeleteOnClose"/>: the OS deletes it when the handle closes, including when the holder crashes, so a stale lock
/// never blocks the next run. A file left behind anyway (for example by an older build) is simply opened and overwritten.
/// </para>
/// <para>Anything that enumerates the project folder must skip <see cref="FileName"/> (see <see cref="IsLockFile"/>).</para>
/// </remarks>
public sealed class ProjectLock : IDisposable
{
    /// <summary>The lock file name inside the project folder.</summary>
    public const string FileName = ".aetherium.lock";

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int DeletePendingRetries = 10;

    private readonly FileStream _stream;
    private int _disposed;

    private ProjectLock(FileStream stream, string lockPath, LockOwnerInfo owner)
    {
        _stream = stream;
        LockPath = lockPath;
        Owner = owner;
    }

    /// <summary>Gets the full path of the lock file.</summary>
    public string LockPath { get; }

    /// <summary>Gets the owner record this process wrote.</summary>
    public LockOwnerInfo Owner { get; }

    /// <summary>Gets the lock file path for a project folder.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns><c>&lt;projectRoot&gt;\.aetherium.lock</c> as a full path.</returns>
    public static string GetLockPath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        return Path.Combine(Path.GetFullPath(projectRoot), FileName);
    }

    /// <summary>Tells whether <paramref name="path"/> is a project lock file (to exclude it from project scans).</summary>
    /// <param name="path">A file path or name.</param>
    /// <returns><see langword="true"/> for <c>.aetherium.lock</c>.</returns>
    public static bool IsLockFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Takes the project lock.</summary>
    /// <param name="projectRoot">The project folder; it must exist.</param>
    /// <param name="ownerDescription">What this process is, for example <c>Aetherium Forge</c> or <c>aetherium-forge scan</c>.</param>
    /// <returns>The lock; dispose it to release.</returns>
    /// <exception cref="ProjectLockedException">Another process (or another lock in this process) holds the project.</exception>
    /// <exception cref="DirectoryNotFoundException">The project folder does not exist.</exception>
    public static ProjectLock Acquire(string projectRoot, string ownerDescription)
    {
        if (TryAcquire(projectRoot, ownerDescription, out var projectLock, out var holder))
        {
            return projectLock;
        }

        throw new ProjectLockedException(GetLockPath(projectRoot), holder);
    }

    /// <summary>Tries to take the project lock.</summary>
    /// <param name="projectRoot">The project folder; it must exist.</param>
    /// <param name="ownerDescription">What this process is.</param>
    /// <param name="projectLock">The lock when taken.</param>
    /// <param name="holder">Who holds the lock when it is taken by someone else, if that could be read.</param>
    /// <returns><see langword="true"/> when the lock was taken.</returns>
    /// <exception cref="DirectoryNotFoundException">The project folder does not exist.</exception>
    public static bool TryAcquire(string projectRoot, string ownerDescription, [NotNullWhen(true)] out ProjectLock? projectLock, out LockOwnerInfo? holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerDescription);

        var lockPath = GetLockPath(projectRoot);
        projectLock = null;
        holder = null;

        for (var attempt = 0; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                holder = ReadOwner(projectRoot);
                return false;
            }
            catch (UnauthorizedAccessException) when (attempt < DeletePendingRetries)
            {
                // The previous holder's file is being deleted (delete pending): it disappears in a moment.
                Thread.Sleep(20);
                continue;
            }

            try
            {
                var owner = LockOwnerInfo.ForCurrentProcess(ownerDescription);
                var bytes = Encoding.UTF8.GetBytes(owner.Serialize());
                stream.SetLength(0);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                projectLock = new ProjectLock(stream, lockPath, owner);
                return true;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>Reads who holds the project lock.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The owner, or <see langword="null"/> when the project is not locked or the owner cannot be read.</returns>
    public static LockOwnerInfo? ReadOwner(string projectRoot)
    {
        var lockPath = GetLockPath(projectRoot);
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return LockOwnerInfo.TryParse(reader.ReadToEnd());
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
            // Diagnostics only: a lock whose owner cannot be read is still reported as locked by the caller.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Releases the lock; the OS deletes the lock file.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }
}
