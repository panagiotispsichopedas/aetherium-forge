using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Finds Skyrim Special Edition installations from Steam, the Bethesda launcher, GOG and Epic (IMPLEMENTATION_GUIDE §4.1).
/// </summary>
/// <remarks>
/// Discovery is best effort: a source that fails (access denied, malformed manifest) is logged and skipped,
/// and the user can always pick a folder manually through <see cref="Validate"/>.
/// </remarks>
public sealed partial class GameLocator
{
    /// <summary>Steam app id of Skyrim Special Edition.</summary>
    public const string SteamAppId = "489830";

    /// <summary>Executable that identifies a Skyrim Special Edition game folder.</summary>
    public const string ExecutableName = "SkyrimSE.exe";

    private const string SteamUserKey = @"Software\Valve\Steam";
    private const string SteamMachineKey = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string BethesdaKey = @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition";
    private const string GogGamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
    private const string GameNameFragment = "Skyrim";

    private readonly IRegistryReader _registry;
    private readonly IFileSystemProbe _fileSystem;
    private readonly string _epicManifestsFolder;
    private readonly ILogger<GameLocator> _logger;

    /// <summary>
    /// Initialises a locator that reads Epic manifests from the default launcher folder
    /// (<c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests</c>).
    /// </summary>
    /// <param name="registry">Registry access.</param>
    /// <param name="fileSystem">File-system access.</param>
    public GameLocator(IRegistryReader registry, IFileSystemProbe fileSystem)
        : this(registry, fileSystem, DefaultEpicManifestsFolder, logger: null)
    {
    }

    /// <summary>
    /// Initialises a locator with an explicit Epic manifests folder and an optional logger.
    /// </summary>
    /// <param name="registry">Registry access.</param>
    /// <param name="fileSystem">File-system access.</param>
    /// <param name="epicManifestsFolder">Folder that holds the Epic launcher's <c>*.item</c> manifests.</param>
    /// <param name="logger">Receives a warning for every discovery source that fails; <see langword="null"/> disables logging.</param>
    public GameLocator(IRegistryReader registry, IFileSystemProbe fileSystem, string epicManifestsFolder, ILogger<GameLocator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(epicManifestsFolder);

        _registry = registry;
        _fileSystem = fileSystem;
        _epicManifestsFolder = epicManifestsFolder;
        _logger = logger ?? NullLogger<GameLocator>.Instance;
    }

    /// <summary>
    /// Gets the default Epic Games Launcher manifests folder.
    /// </summary>
    public static string DefaultEpicManifestsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    /// <summary>
    /// Searches every known source, in the order Steam, Bethesda, GOG, Epic, and returns the valid installations.
    /// </summary>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>Valid installations, de-duplicated by game folder (first source wins).</returns>
    public async Task<IReadOnlyList<GameInstall>> FindAllAsync(CancellationToken ct = default)
    {
        var candidates = new List<(string Folder, GameStore Store)>();

        await CollectAsync(GameStore.Steam, candidates, FindSteamFoldersAsync, ct).ConfigureAwait(false);
        await CollectAsync(GameStore.Bethesda, candidates, _ => Task.FromResult(FindBethesdaFolders()), ct).ConfigureAwait(false);
        await CollectAsync(GameStore.Gog, candidates, _ => Task.FromResult<IReadOnlyList<string>>(FindGogFolders()), ct).ConfigureAwait(false);
        await CollectAsync(GameStore.Epic, candidates, FindEpicFoldersAsync, ct).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installs = new List<GameInstall>();
        foreach (var (folder, store) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var install = Validate(folder, store);
            if (install is not null && seen.Add(install.GameFolder))
            {
                installs.Add(install);
            }
        }

        return installs;
    }

    /// <summary>
    /// Checks that a folder is a Skyrim Special Edition game folder: it holds <c>SkyrimSE.exe</c> and a <c>Data</c> folder.
    /// </summary>
    /// <param name="gameFolder">The candidate game folder.</param>
    /// <param name="store">How the folder was found; <see cref="GameStore.Manual"/> for a user choice.</param>
    /// <returns>The validated installation, or <see langword="null"/> when the folder is not a game folder.</returns>
    public GameInstall? Validate(string gameFolder, GameStore store = GameStore.Manual)
    {
        ArgumentNullException.ThrowIfNull(gameFolder);

        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            return null;
        }

        var folder = NormalizeFolder(gameFolder);
        var exePath = Path.Combine(folder, ExecutableName);
        var dataFolder = Path.Combine(folder, "Data");
        if (!_fileSystem.FileExists(exePath) || !_fileSystem.DirectoryExists(dataFolder))
        {
            return null;
        }

        return new GameInstall(folder, dataFolder, store, _fileSystem.GetFileVersion(exePath));
    }

    private static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Replace('/', '\\')));

    private async Task CollectAsync(
        GameStore store,
        List<(string Folder, GameStore Store)> candidates,
        Func<CancellationToken, Task<IReadOnlyList<string>>> source,
        CancellationToken ct)
    {
        try
        {
            foreach (var folder in await source(ct).ConfigureAwait(false))
            {
                candidates.Add((folder, store));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or FormatException or JsonException or ArgumentException)
        {
            LogSourceFailed(_logger, exception, store);
        }
    }

    private async Task<IReadOnlyList<string>> FindSteamFoldersAsync(CancellationToken ct)
    {
        var steamPath = _registry.GetString(RegistryHive.CurrentUser, SteamUserKey, "SteamPath")
            ?? _registry.GetString(RegistryHive.LocalMachine, SteamMachineKey, "InstallPath");
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return [];
        }

        steamPath = NormalizeFolder(steamPath);
        var libraries = new List<string> { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (_fileSystem.FileExists(libraryFile))
        {
            var document = VdfParser.Parse(await _fileSystem.ReadAllTextAsync(libraryFile, ct).ConfigureAwait(false));
            libraries.AddRange(ReadLibraryPaths(document));
        }

        var folders = new List<string>();
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (!_fileSystem.FileExists(manifestPath))
            {
                continue;
            }

            var manifest = VdfParser.Parse(await _fileSystem.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false));
            var installDir = manifest["AppState"]?["installdir"]?.Value;
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                folders.Add(Path.Combine(library, "steamapps", "common", installDir));
            }
        }

        return folders;
    }

    private static IEnumerable<string> ReadLibraryPaths(VdfNode document)
    {
        var root = document["libraryfolders"];
        if (root is null)
        {
            yield break;
        }

        foreach (var entry in root.Children)
        {
            // Current format: "0" { "path" "D:\\SteamLibrary" "apps" { ... } }.
            // Legacy format:  "1" "D:\\SteamLibrary" next to non-numeric keys such as "TimeNextStatsReport".
            var path = entry.Value is null
                ? entry["path"]?.Value
                : entry.Key.All(char.IsAsciiDigit) ? entry.Value : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return NormalizeFolder(path);
            }
        }
    }

    private IReadOnlyList<string> FindBethesdaFolders()
    {
        var path = _registry.GetString(RegistryHive.LocalMachine, BethesdaKey, "installed path");

        return string.IsNullOrWhiteSpace(path) ? [] : [path];
    }

    private List<string> FindGogFolders()
    {
        var folders = new List<string>();
        foreach (var gameId in _registry.GetSubKeyNames(RegistryHive.LocalMachine, GogGamesKey))
        {
            var gameKey = $@"{GogGamesKey}\{gameId}";
            var gameName = _registry.GetString(RegistryHive.LocalMachine, gameKey, "gameName");
            if (gameName is null || !gameName.Contains(GameNameFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = _registry.GetString(RegistryHive.LocalMachine, gameKey, "path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                folders.Add(path);
            }
        }

        return folders;
    }

    private async Task<IReadOnlyList<string>> FindEpicFoldersAsync(CancellationToken ct)
    {
        var folders = new List<string>();
        foreach (var manifestPath in _fileSystem.EnumerateFiles(_epicManifestsFolder, "*.item"))
        {
            try
            {
                var json = await _fileSystem.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("DisplayName", out var displayName)
                    && displayName.ValueKind == JsonValueKind.String
                    && displayName.GetString()!.Contains(GameNameFragment, StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("InstallLocation", out var installLocation)
                    && installLocation.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(installLocation.GetString()))
                {
                    folders.Add(installLocation.GetString()!);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // One unreadable manifest must not hide the other installed games.
                LogManifestFailed(_logger, exception, manifestPath);
            }
        }

        return folders;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Game discovery through {Store} failed; skipping that source")]
    private static partial void LogSourceFailed(ILogger logger, Exception exception, GameStore store);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipping unreadable Epic manifest {ManifestPath}")]
    private static partial void LogManifestFailed(ILogger logger, Exception exception, string manifestPath);
}
