using AetheriumForge.Core.Archives;
using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Provides the original bytes of a game texture.
/// </summary>
public interface ITextureSource
{
    /// <summary>
    /// Reads the full file for <paramref name="path"/> from <paramref name="archiveName"/>.
    /// </summary>
    /// <param name="path">Canonical resource path.</param>
    /// <param name="archiveName">Archive file name in <c>Data</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The DDS bytes.</returns>
    Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ITextureSource"/> that reads from the game's BSA archives, keeping one open reader per archive.
/// </summary>
/// <remarks>
/// Archives are opened on first use under a lock shared with <see cref="Dispose"/>, so a reader is never opened after
/// disposal and left undisposed. A failed open is not cached: the next read tries again.
/// </remarks>
public sealed class ArchiveTextureSource : ITextureSource, IDisposable
{
    private readonly string _dataFolder;
    private readonly Dictionary<string, (BsaReader Reader, Dictionary<string, BsaEntry> Entries)> _archives = new(GamePath.Comparer);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveTextureSource"/> class.
    /// </summary>
    /// <param name="dataFolder">The game's <c>Data</c> folder.</param>
    public ArchiveTextureSource(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        _dataFolder = dataFolder;
    }

    /// <inheritdoc />
    /// <remarks>Archive, entry and disposal errors are returned as a faulted task, not thrown synchronously.</remarks>
    /// <exception cref="FileNotFoundException">The archive or the entry does not exist (faulted task).</exception>
    /// <exception cref="ObjectDisposedException">The source has been disposed (faulted task).</exception>
    public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(archiveName);

        (BsaReader Reader, Dictionary<string, BsaEntry> Entries) archive;
        try
        {
            archive = GetArchive(archiveName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or NotSupportedException or ArgumentException)
        {
            // Not swallowed: the caller observes it through the returned task, like any other asynchronous failure.
            return Task.FromException<byte[]>(ex);
        }

        var canonical = GamePath.Normalize(path);
        if (!archive.Entries.TryGetValue(canonical, out var entry))
        {
            return Task.FromException<byte[]>(new FileNotFoundException($"{canonical} is not in {archiveName}.", canonical));
        }

        return Task.Run(() => archive.Reader.ReadFile(entry), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var archive in _archives.Values)
            {
                archive.Reader.Dispose();
            }

            _archives.Clear();
        }
    }

    private (BsaReader Reader, Dictionary<string, BsaEntry> Entries) GetArchive(string archiveName)
    {
        // Opening under the lock serialises first opens (a one-off directory read per archive), but it guarantees that
        // Dispose either sees the new reader in the map or runs first and makes this call fail. A failed open adds nothing,
        // so a transient error does not poison the archive.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_archives.TryGetValue(archiveName, out var archive))
            {
                archive = Open(archiveName);
                _archives.Add(archiveName, archive);
            }

            return archive;
        }
    }

    private (BsaReader Reader, Dictionary<string, BsaEntry> Entries) Open(string archiveName)
    {
        var reader = BsaReader.Open(Path.Combine(_dataFolder, archiveName));
        try
        {
            var entries = new Dictionary<string, BsaEntry>(StringComparer.Ordinal);
            foreach (var entry in reader.Entries)
            {
                entries[entry.Path] = entry;
            }

            return (reader, entries);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }
}
