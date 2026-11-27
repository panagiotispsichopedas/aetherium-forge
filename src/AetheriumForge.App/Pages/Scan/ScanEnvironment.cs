using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>
/// The discovery and scan operations the Scan page uses (IMPLEMENTATION_GUIDE §4), behind one seam so the view model can be tested with fakes.
/// </summary>
public interface IScanEnvironment
{
    /// <summary>Finds the installed games (Steam, GOG, Epic, registry).</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The installs found, best first.</returns>
    Task<IReadOnlyList<GameInstall>> FindInstallsAsync(CancellationToken cancellationToken);

    /// <summary>Validates a game folder chosen by the user.</summary>
    /// <param name="gameFolder">The folder.</param>
    /// <param name="store">The store to record for it.</param>
    /// <returns>The install, or <see langword="null"/> when the folder is not a Skyrim Special Edition folder.</returns>
    GameInstall? ValidateInstall(string gameFolder, GameStore store);

    /// <summary>Locates the profile files (INI folder, <c>plugins.txt</c>) for a store.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The profile paths.</returns>
    GameProfilePaths LocateProfile(GameStore store);

    /// <summary>Loads the profile (INI chain, load order, archives).</summary>
    /// <param name="game">The game.</param>
    /// <param name="paths">The profile paths.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The profile.</returns>
    Task<GameProfile> LoadProfileAsync(GameInstall game, GameProfilePaths paths, CancellationToken cancellationToken);

    /// <summary>Scans the gap set, off the UI thread.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="rules">The rules.</param>
    /// <param name="progress">Phase progress.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The scan.</returns>
    Task<ScanResult> ScanAsync(GameProfile profile, ForgeRules rules, IProgress<ScanProgress> progress, CancellationToken cancellationToken);

    /// <summary>Lists the fixed drives with their free space.</summary>
    /// <returns>The drives.</returns>
    IReadOnlyList<(string Root, long Free, bool IsSystem)> GetFixedDrives();

    /// <summary>Tells whether a file exists.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    bool FileExists(string path);

    /// <summary>Reads one value of a project database without opening a job store.</summary>
    /// <param name="databasePath">The project's <c>forge.db</c>.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    Task<string?> ReadProjectValueAsync(string databasePath, string key, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IScanEnvironment"/>: Core's <see cref="GameLocator"/>, <see cref="ProfileLocator"/>, <see cref="GameProfileLoader"/>,
/// <see cref="TextureScanner"/> and <see cref="DriveAdvisor"/>.
/// </summary>
public sealed class ScanEnvironment : IScanEnvironment
{
    private readonly GameLocator _gameLocator;
    private readonly ProfileLocator _profileLocator;
    private readonly TextureScanner _scanner;

    /// <summary>Initializes a new instance of the <see cref="ScanEnvironment"/> class.</summary>
    /// <param name="gameLocator">Game discovery.</param>
    /// <param name="profileLocator">Profile discovery.</param>
    /// <param name="scanner">The texture scanner.</param>
    public ScanEnvironment(GameLocator gameLocator, ProfileLocator profileLocator, TextureScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(gameLocator);
        ArgumentNullException.ThrowIfNull(profileLocator);
        ArgumentNullException.ThrowIfNull(scanner);

        _gameLocator = gameLocator;
        _profileLocator = profileLocator;
        _scanner = scanner;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GameInstall>> FindInstallsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _gameLocator.FindAllAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public GameInstall? ValidateInstall(string gameFolder, GameStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        return _gameLocator.Validate(Path.GetFullPath(gameFolder), store);
    }

    /// <inheritdoc />
    public GameProfilePaths LocateProfile(GameStore store) => _profileLocator.Locate(store);

    /// <inheritdoc />
    public Task<GameProfile> LoadProfileAsync(GameInstall game, GameProfilePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(paths);

        return GameProfileLoader.LoadAsync(game.GameFolder, paths.IniFolder, paths.PluginsTxtPath, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ScanResult> ScanAsync(GameProfile profile, ForgeRules rules, IProgress<ScanProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => _scanner.ScanAsync(profile, rules, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<(string Root, long Free, bool IsSystem)> GetFixedDrives() => DriveAdvisor.GetFixedDrives();

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public Task<string?> ReadProjectValueAsync(string databasePath, string key, CancellationToken cancellationToken) =>
        ProjectDatabaseReader.ReadProjectValueAsync(databasePath, key, cancellationToken);
}
