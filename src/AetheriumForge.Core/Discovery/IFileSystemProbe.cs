namespace AetheriumForge.Core.Discovery;

/// <summary>
/// The small slice of file-system access discovery needs, abstracted so it can be tested without real files.
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>
    /// Determines whether a file exists.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>
    /// Determines whether a directory exists.
    /// </summary>
    /// <param name="path">Absolute directory path.</param>
    /// <returns><see langword="true"/> when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>
    /// Reads a whole text file.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file content.</returns>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the files directly inside a directory (not recursive) that match a search pattern.
    /// </summary>
    /// <param name="directory">Absolute directory path.</param>
    /// <param name="searchPattern">A pattern such as <c>*.item</c>.</param>
    /// <returns>Absolute file paths; empty when the directory does not exist.</returns>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);

    /// <summary>
    /// Reads the file version resource of an executable.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns>The file version, or <see langword="null"/> when the file has no usable version resource.</returns>
    Version? GetFileVersion(string path);
}
