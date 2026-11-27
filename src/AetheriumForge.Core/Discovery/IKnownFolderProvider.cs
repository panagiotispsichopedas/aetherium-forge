namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Resolves the per-user shell folders that hold Skyrim's profile files.
/// </summary>
public interface IKnownFolderProvider
{
    /// <summary>
    /// Gets the user's Documents folder, honouring a relocated Documents folder.
    /// </summary>
    /// <returns>The absolute Documents path.</returns>
    string GetDocumentsPath();

    /// <summary>
    /// Gets the user's local application data folder (<c>%LOCALAPPDATA%</c>).
    /// </summary>
    /// <returns>The absolute local application data path.</returns>
    string GetLocalApplicationDataPath();
}
