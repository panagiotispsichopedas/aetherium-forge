using AetheriumForge.Core.Discovery;
using Microsoft.Win32;

namespace AetheriumForge.Core.Tests.Discovery;

public sealed class GameLocatorTests
{
    private const string EpicManifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";

    private const string SteamManifest = """
        "AppState"
        {
            "appid"      "489830"
            "installdir" "Skyrim Special Edition"
        }
        """;

    private readonly FakeRegistryReader _registry = new();
    private readonly FakeFileSystemProbe _fileSystem = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GameLocator CreateLocator() => new(_registry, _fileSystem, EpicManifests);

    [Fact]
    public async Task FindAllAsync_SteamSecondaryLibrary_FindsGame()
    {
        // Arrange
        _registry.Set(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", "c:/program files (x86)/steam");
        _fileSystem.AddFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { } }
                "1" { "path" "D:\\SteamLibrary" "apps" { "489830" "1" } }
            }
            """);
        _fileSystem.AddFile(@"D:\SteamLibrary\steamapps\appmanifest_489830.acf", SteamManifest);
        _fileSystem.AddGame(@"D:\SteamLibrary\steamapps\common\Skyrim Special Edition", new Version(1, 6, 1170, 0));

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        var expected = new GameInstall(
            @"D:\SteamLibrary\steamapps\common\Skyrim Special Edition",
            @"D:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data",
            GameStore.Steam,
            new Version(1, 6, 1170, 0));
        Assert.Equal([expected], installs);
    }

    [Fact]
    public async Task FindAllAsync_SteamMachineKeyFallbackAndLegacyLibraryFormat_FindsGame()
    {
        // Arrange
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", SteamRoot);
        _fileSystem.AddFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", """
            "LibraryFolders"
            {
                "TimeNextStatsReport" "1234567890"
                "ContentStatsID"      "-123"
                "1"                   "E:\\Steam"
            }
            """);
        _fileSystem.AddFile(@"E:\Steam\steamapps\appmanifest_489830.acf", SteamManifest);
        _fileSystem.AddGame(@"E:\Steam\steamapps\common\Skyrim Special Edition");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Equal(@"E:\Steam\steamapps\common\Skyrim Special Edition", Assert.Single(installs).GameFolder);
    }

    [Fact]
    public async Task FindAllAsync_SteamRootLibraryWithoutLibraryFile_FindsGame()
    {
        // Arrange
        _registry.Set(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", SteamRoot);
        _fileSystem.AddFile($@"{SteamRoot}\steamapps\appmanifest_489830.acf", SteamManifest);
        _fileSystem.AddGame($@"{SteamRoot}\steamapps\common\Skyrim Special Edition");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Equal(GameStore.Steam, Assert.Single(installs).Store);
    }

    [Fact]
    public async Task FindAllAsync_BethesdaKey_FindsGame()
    {
        // Arrange
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "installed path", @"F:\Bethesda\Skyrim\");
        _fileSystem.AddGame(@"F:\Bethesda\Skyrim");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Equal(new GameInstall(@"F:\Bethesda\Skyrim", @"F:\Bethesda\Skyrim\Data", GameStore.Bethesda, null), Assert.Single(installs));
    }

    [Fact]
    public async Task FindAllAsync_GogSubKeys_FindsOnlySkyrim()
    {
        // Arrange
        _registry
            .Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1207658930", "gameName", "The Witcher 3")
            .Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1207658930", "path", @"G:\GOG\Witcher3")
            .Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1711230643", "gameName", "The Elder Scrolls V: Skyrim Anniversary Edition")
            .Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1711230643", "path", @"G:\GOG\Skyrim AE");
        _fileSystem.AddGame(@"G:\GOG\Witcher3").AddGame(@"G:\GOG\Skyrim AE");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        var install = Assert.Single(installs);
        Assert.Equal((@"G:\GOG\Skyrim AE", GameStore.Gog), (install.GameFolder, install.Store));
    }

    [Fact]
    public async Task FindAllAsync_EpicManifests_FindsOnlySkyrimAndSkipsMalformed()
    {
        // Arrange
        _fileSystem
            .AddFile($@"{EpicManifests}\A.item", """{ "DisplayName": "Fortnite", "InstallLocation": "H:\\Epic\\Fortnite" }""")
            .AddFile($@"{EpicManifests}\B.item", """{ "DisplayName": "The Elder Scrolls V: Skyrim Special Edition", "InstallLocation": "H:\\Epic\\SkyrimSE" }""")
            .AddFile($@"{EpicManifests}\C.item", "{ not json")
            .AddFile($@"{EpicManifests}\D.txt", """{ "DisplayName": "Skyrim", "InstallLocation": "H:\\Other" }""")
            .AddGame(@"H:\Epic\Fortnite")
            .AddGame(@"H:\Epic\SkyrimSE")
            .AddGame(@"H:\Other");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        var install = Assert.Single(installs);
        Assert.Equal((@"H:\Epic\SkyrimSE", GameStore.Epic), (install.GameFolder, install.Store));
    }

    [Fact]
    public async Task FindAllAsync_CandidateWithoutExecutableOrData_IsRejected()
    {
        // Arrange
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "installed path", @"F:\NoData");
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1", "gameName", "Skyrim");
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games\1", "path", @"G:\NoExe");
        _fileSystem.AddFile(@"F:\NoData\SkyrimSE.exe").AddDirectory(@"G:\NoExe\Data");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Empty(installs);
    }

    [Fact]
    public async Task FindAllAsync_SameFolderFromTwoStores_ReturnsItOnceWithFirstStore()
    {
        // Arrange
        _registry.Set(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", SteamRoot);
        _fileSystem.AddFile($@"{SteamRoot}\steamapps\appmanifest_489830.acf", SteamManifest);
        var gameFolder = $@"{SteamRoot}\steamapps\common\Skyrim Special Edition";
        _fileSystem.AddGame(gameFolder);
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "installed path", gameFolder.ToUpperInvariant() + "\\");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Equal(GameStore.Steam, Assert.Single(installs).Store);
    }

    [Fact]
    public async Task FindAllAsync_MalformedSteamManifest_StillFindsOtherStores()
    {
        // Arrange
        _registry.Set(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", SteamRoot);
        _fileSystem.AddFile($@"{SteamRoot}\steamapps\libraryfolders.vdf", "\"libraryfolders\" { \"0\" {");
        _registry.Set(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", "installed path", @"F:\Skyrim");
        _fileSystem.AddGame(@"F:\Skyrim");

        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Equal(GameStore.Bethesda, Assert.Single(installs).Store);
    }

    [Fact]
    public async Task FindAllAsync_NothingInstalled_ReturnsEmpty()
    {
        // Act
        var installs = await CreateLocator().FindAllAsync(Ct);

        // Assert
        Assert.Empty(installs);
    }

    [Fact]
    public void Validate_ValidFolderWithTrailingSeparator_ReturnsManualInstall()
    {
        // Arrange
        _fileSystem.AddGame(@"C:\Games\Skyrim", new Version(1, 5, 97, 0));

        // Act
        var install = CreateLocator().Validate(@"C:/Games/Skyrim/");

        // Assert
        Assert.Equal(new GameInstall(@"C:\Games\Skyrim", @"C:\Games\Skyrim\Data", GameStore.Manual, new Version(1, 5, 97, 0)), install);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Missing")]
    public void Validate_InvalidFolder_ReturnsNull(string folder)
    {
        // Act
        var install = CreateLocator().Validate(folder);

        // Assert
        Assert.Null(install);
    }

    [Fact]
    public void Constructor_NullRegistry_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GameLocator(null!, _fileSystem));
    }
}
