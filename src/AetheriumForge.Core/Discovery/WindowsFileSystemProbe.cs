using System.Diagnostics;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// <see cref="IFileSystemProbe"/> backed by <see cref="File"/>, <see cref="Directory"/> and <see cref="FileVersionInfo"/>.
/// </summary>
public sealed class WindowsFileSystemProbe : IFileSystemProbe
{
    /// <inheritdoc />
    public bool FileExists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return File.Exists(path);
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Directory.Exists(path);
    }

    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        return File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(searchPattern);

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            : [];
    }

    /// <inheritdoc />
    public Version? GetFileVersion(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0)
        {
            return null;
        }

        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
}
