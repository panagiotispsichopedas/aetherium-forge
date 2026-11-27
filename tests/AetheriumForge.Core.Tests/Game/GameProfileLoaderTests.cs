using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class GameProfileLoaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameFolder;
    private readonly string _dataFolder;
    private readonly string _iniFolder;
    private readonly string _pluginsTxtPath;
    private readonly string _crashLogPath;

    public GameProfileLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"af-game-{Guid.NewGuid():N}");
        _gameFolder = Path.Combine(_root, "Skyrim Special Edition");
        _dataFolder = Path.Combine(_gameFolder, "Data");
        _iniFolder = Path.Combine(_root, "My Games");
        _pluginsTxtPath = Path.Combine(_root, "AppData", "plugins.txt");
        _crashLogPath = Path.Combine(_root, "crash-2026-11-27.log");

        Directory.CreateDirectory(_dataFolder);
        Directory.CreateDirectory(_iniFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(_pluginsTxtPath)!);

        File.WriteAllText(Path.Combine(_gameFolder, "Skyrim_Default.ini"), "[Archive]\nsResourceArchiveList=Skyrim - Misc.bsa\nsResourceArchiveList2=Skyrim - Textures0.bsa\n");
        File.WriteAllText(Path.Combine(_iniFolder, "SkyrimCustom.ini"), "[Archive]\nsResourceArchiveList2=Skyrim - Textures0.bsa, Skyrim - Textures1.bsa\n");
        File.WriteAllText(Path.Combine(_gameFolder, "Skyrim.ccc"), "ccBGSSSE001-Fish.esm\nccMissing.esm\n");

        foreach (string file in new[] { "Skyrim.esm", "ccBGSSSE001-Fish.esm", "ccBGSSSE001-Fish.bsa", "ModA.esp", "ModA - Textures.bsa", "ModB.esp" })
        {
            File.WriteAllText(Path.Combine(_dataFolder, file), string.Empty);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_AllFilesPresent_BuildsLoadOrder()
    {
        // Arrange
        await File.WriteAllTextAsync(_pluginsTxtPath, "# comment\n*ModA.esp\nModB.esp\n", TestContext.Current.CancellationToken);

        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, _pluginsTxtPath, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "ccBGSSSE001-Fish.esm", "ModA.esp"], result.LoadOrder);
    }

    [Fact]
    public async Task LoadAsync_AllFilesPresent_ResolvesIniArchivesWithOverride()
    {
        // Arrange
        await File.WriteAllTextAsync(_pluginsTxtPath, "*ModA.esp\n", TestContext.Current.CancellationToken);

        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, _pluginsTxtPath, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["Skyrim - Misc.bsa", "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa"], result.IniArchives);
    }

    [Fact]
    public async Task LoadAsync_AllFilesPresent_ListsExistingPluginArchives()
    {
        // Arrange
        await File.WriteAllTextAsync(_pluginsTxtPath, "*ModA.esp\n", TestContext.Current.CancellationToken);

        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, _pluginsTxtPath, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["ccBGSSSE001-Fish.bsa", "ModA - Textures.bsa"], result.PluginArchives);
    }

    [Fact]
    public async Task LoadAsync_PluginsTxtMissing_FallsBackToCrashLoggerLog()
    {
        // Arrange
        await File.WriteAllTextAsync(_crashLogPath, "PLUGINS:\n\tLight: 0\tRegular: 2\tTotal: 2\n\t[00]     Skyrim.esm\n\t[01]     ModB.esp\n", TestContext.Current.CancellationToken);

        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, _pluginsTxtPath, _crashLogPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("ModB.esp", result.LoadOrder[^1]);
    }

    [Fact]
    public async Task LoadAsync_NoPluginSources_ReturnsImplicitAndCcOnly()
    {
        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(6, result.LoadOrder.Count);
    }

    [Fact]
    public async Task LoadAsync_EmptyFolders_ReturnsEmptyArchives()
    {
        // Arrange
        string emptyGame = Path.Combine(_root, "EmptyGame");
        Directory.CreateDirectory(emptyGame);

        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(emptyGame, Path.Combine(_root, "NoIni"), null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.IniArchives);
    }

    [Fact]
    public async Task LoadAsync_AnyCall_SetsDataFolder()
    {
        // Act
        GameProfile result = await GameProfileLoader.LoadAsync(_gameFolder, _iniFolder, null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(_dataFolder, result.DataFolder);
    }
}
