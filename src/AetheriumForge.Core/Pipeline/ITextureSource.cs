using System.Collections.Concurrent;
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
public sealed class ArchiveTextureSource : ITextureSource, IDisposable
{
    private readonly string _dataFolder;
    private readonly ConcurrentDictionary<string, Lazy<(BsaReader Reader, Dictionary<string, BsaEntry> Entries)>> _archives = new(GamePath.Comparer);
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
    /// <exception cref="FileNotFoundException">The archive or the entry does not exist.</exception>
    public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(archiveName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var archive = _archives.GetOrAdd(archiveName, name => new Lazy<(BsaReader, Dictionary<string, BsaEntry>)>(() => Open(name))).Value;
        var canonical = GamePath.Normalize(path);
        if (!archive.Entries.TryGetValue(canonical, out var entry))
        {
            throw new FileNotFoundException($"{canonical} is not in {archiveName}.", canonical);
        }

        return Task.Run(() => archive.Reader.ReadFile(entry), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var archive in _archives.Values)
        {
            if (archive.IsValueCreated)
            {
                archive.Value.Reader.Dispose();
            }
        }
    }

    private (BsaReader Reader, Dictionary<string, BsaEntry> Entries) Open(string archiveName)
    {
        var reader = BsaReader.Open(Path.Combine(_dataFolder, archiveName));
        var entries = new Dictionary<string, BsaEntry>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
        {
            entries[entry.Path] = entry;
        }

        return (reader, entries);
    }
}
