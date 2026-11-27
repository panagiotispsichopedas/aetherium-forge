namespace AetheriumForge.Core.Discovery;

/// <summary>
/// <see cref="IKnownFolderProvider"/> backed by <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>.
/// </summary>
/// <remarks>
/// Users often move Documents to another drive, so the path is never built from <c>%USERPROFILE%</c> (IMPLEMENTATION_GUIDE §4.2).
/// On Windows, .NET resolves these folders with the Known Folder API (<c>SHGetKnownFolderPath</c>), so redirected folders are
/// honoured without any P/Invoke in this project.
/// </remarks>
public sealed class WindowsKnownFolderProvider : IKnownFolderProvider
{
    /// <inheritdoc />
    public string GetDocumentsPath() => GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <inheritdoc />
    public string GetLocalApplicationDataPath() => GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string GetFolderPath(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
        {
            // GetFolderPath returns an empty string when the folder does not exist or cannot be resolved.
            throw new DirectoryNotFoundException($"The known folder {folder} could not be resolved.");
        }

        return path;
    }
}
