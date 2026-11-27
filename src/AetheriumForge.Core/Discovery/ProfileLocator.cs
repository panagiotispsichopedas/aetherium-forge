namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Resolves the INI folder and <c>plugins.txt</c> location for a store (IMPLEMENTATION_GUIDE §4.2).
/// </summary>
public sealed class ProfileLocator
{
    private const string BaseFolderName = "Skyrim Special Edition";

    private readonly IKnownFolderProvider _knownFolders;

    /// <summary>
    /// Initialises a locator that uses the Windows Known Folder API.
    /// </summary>
    public ProfileLocator()
        : this(new WindowsKnownFolderProvider())
    {
    }

    /// <summary>
    /// Initialises a locator with a custom known-folder source.
    /// </summary>
    /// <param name="knownFolders">Resolves Documents and local application data.</param>
    public ProfileLocator(IKnownFolderProvider knownFolders)
    {
        ArgumentNullException.ThrowIfNull(knownFolders);

        _knownFolders = knownFolders;
    }

    /// <summary>
    /// Gets the profile folder name for a store: <c>Skyrim Special Edition</c>, with a <c>GOG</c> or <c>EPIC</c> suffix for those stores.
    /// </summary>
    /// <param name="store">The store the game was installed through.</param>
    /// <returns>The folder name used under both <c>Documents\My Games</c> and <c>%LOCALAPPDATA%</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="store"/> is not a defined value.</exception>
    public static string GetProfileFolderName(GameStore store) => store switch
    {
        GameStore.Steam or GameStore.Bethesda or GameStore.Manual => BaseFolderName,
        GameStore.Gog => $"{BaseFolderName} GOG",
        GameStore.Epic => $"{BaseFolderName} EPIC",
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, "Unknown game store."),
    };

    /// <summary>
    /// Locates the profile files for a store.
    /// </summary>
    /// <param name="store">The store the game was installed through.</param>
    /// <returns>The INI folder and the <c>plugins.txt</c> path. The files themselves may not exist yet.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="store"/> is not a defined value.</exception>
    public GameProfilePaths Locate(GameStore store)
    {
        var folderName = GetProfileFolderName(store);
        var iniFolder = Path.Combine(_knownFolders.GetDocumentsPath(), "My Games", folderName);
        var pluginsTxt = Path.Combine(_knownFolders.GetLocalApplicationDataPath(), folderName, "plugins.txt");

        return new GameProfilePaths(iniFolder, pluginsTxt);
    }
}
