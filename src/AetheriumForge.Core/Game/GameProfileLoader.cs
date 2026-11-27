namespace AetheriumForge.Core.Game;

/// <summary>
/// Loads a <see cref="GameProfile"/> from the game folder and the user's profile files (IMPLEMENTATION_GUIDE §4.2 and §5).
/// </summary>
/// <remarks>File IO is confined to this class; all parsing is delegated to the pure parsers in this namespace.</remarks>
public static class GameProfileLoader
{
    /// <summary>The name of the game's data folder.</summary>
    public const string DataFolderName = "Data";

    /// <summary>The default INI, read from the game folder.</summary>
    public const string DefaultIniFileName = "Skyrim_Default.ini";

    /// <summary>The user INI, read from the INI folder.</summary>
    public const string UserIniFileName = "Skyrim.ini";

    /// <summary>The custom INI, read from the INI folder.</summary>
    public const string CustomIniFileName = "SkyrimCustom.ini";

    /// <summary>The Creation Club plugin list, read from the game folder.</summary>
    public const string CccFileName = "Skyrim.ccc";

    /// <summary>
    /// Reads the INI chain, the plugin list (or the CrashLogger fallback) and <c>Skyrim.ccc</c>, and builds the profile.
    /// </summary>
    /// <param name="gameFolder">The game folder (contains <c>Data</c>, <c>Skyrim_Default.ini</c> and <c>Skyrim.ccc</c>).</param>
    /// <param name="iniFolder">The folder holding <c>Skyrim.ini</c> and <c>SkyrimCustom.ini</c> (Documents\My Games\...).</param>
    /// <param name="pluginsTxtPath">The path of <c>plugins.txt</c>; <see langword="null"/> or missing falls back to <paramref name="crashLoggerLogPath"/>.</param>
    /// <param name="crashLoggerLogPath">Optional CrashLogger log used when <c>plugins.txt</c> is missing; when it is also missing, no active plugins are used.</param>
    /// <param name="cancellationToken">Cancels the file reads.</param>
    /// <returns>The resolved profile. Missing optional files are treated as absent, not as errors.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gameFolder"/> or <paramref name="iniFolder"/> is <see langword="null"/>.</exception>
    public static async Task<GameProfile> LoadAsync(
        string gameFolder,
        string iniFolder,
        string? pluginsTxtPath,
        string? crashLoggerLogPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameFolder);
        ArgumentNullException.ThrowIfNull(iniFolder);

        string dataFolder = Path.Combine(gameFolder, DataFolderName);
        bool ExistsInData(string fileName) => File.Exists(Path.Combine(dataFolder, fileName));

        string[] iniPaths =
        [
            Path.Combine(gameFolder, DefaultIniFileName),
            Path.Combine(iniFolder, UserIniFileName),
            Path.Combine(iniFolder, CustomIniFileName),
        ];

        var iniContents = new List<string>(iniPaths.Length);
        foreach (string iniPath in iniPaths)
        {
            string? content = await ReadOptionalAsync(iniPath, cancellationToken).ConfigureAwait(false);
            if (content is not null)
            {
                iniContents.Add(content);
            }
        }

        IReadOnlyList<string> iniArchives = IniArchiveList.Resolve(iniContents);
        IReadOnlyList<string> activePlugins = await LoadActivePluginsAsync(pluginsTxtPath, crashLoggerLogPath, cancellationToken).ConfigureAwait(false);

        string? cccContent = await ReadOptionalAsync(Path.Combine(gameFolder, CccFileName), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> cccEntries = cccContent is null ? [] : PluginListParser.ParseCcc(cccContent);

        IReadOnlyList<string> loadOrder = LoadOrderBuilder.Build(activePlugins, cccEntries, ExistsInData);
        IReadOnlyList<string> pluginArchives = PluginArchives.For(loadOrder, ExistsInData);

        return new GameProfile(dataFolder, loadOrder, iniArchives, pluginArchives);
    }

    private static async Task<IReadOnlyList<string>> LoadActivePluginsAsync(string? pluginsTxtPath, string? crashLoggerLogPath, CancellationToken cancellationToken)
    {
        string? pluginsTxt = await ReadOptionalAsync(pluginsTxtPath, cancellationToken).ConfigureAwait(false);
        if (pluginsTxt is not null)
        {
            return PluginListParser.ParseActive(pluginsTxt);
        }

        string? crashLog = await ReadOptionalAsync(crashLoggerLogPath, cancellationToken).ConfigureAwait(false);
        return crashLog is null ? [] : PluginListParser.ParseCrashLoggerPlugins(crashLog);
    }

    private static async Task<string?> ReadOptionalAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
