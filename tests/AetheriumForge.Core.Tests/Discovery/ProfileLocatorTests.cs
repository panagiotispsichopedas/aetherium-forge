using AetheriumForge.Core.Discovery;

namespace AetheriumForge.Core.Tests.Discovery;

public sealed class ProfileLocatorTests
{
    private const string Documents = @"E:\Users\Dragonborn\Docs";
    private const string LocalAppData = @"C:\Users\Dragonborn\AppData\Local";

    [Theory]
    [InlineData(GameStore.Steam, "Skyrim Special Edition")]
    [InlineData(GameStore.Bethesda, "Skyrim Special Edition")]
    [InlineData(GameStore.Manual, "Skyrim Special Edition")]
    [InlineData(GameStore.Gog, "Skyrim Special Edition GOG")]
    [InlineData(GameStore.Epic, "Skyrim Special Edition EPIC")]
    public void Locate_Store_UsesStoreSuffix(GameStore store, string folderName)
    {
        // Arrange
        var locator = new ProfileLocator(new FakeKnownFolderProvider(Documents, LocalAppData));

        // Act
        var paths = locator.Locate(store);

        // Assert
        Assert.Equal(new GameProfilePaths($@"{Documents}\My Games\{folderName}", $@"{LocalAppData}\{folderName}\plugins.txt"), paths);
    }

    [Fact]
    public void Locate_UndefinedStore_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var locator = new ProfileLocator(new FakeKnownFolderProvider(Documents, LocalAppData));

        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => locator.Locate((GameStore)42));
    }

    [Fact]
    public void WindowsKnownFolderProvider_GetDocumentsPath_ReturnsExistingFolder()
    {
        // Act
        var documents = new WindowsKnownFolderProvider().GetDocumentsPath();

        // Assert
        Assert.True(Directory.Exists(documents));
    }

    [Fact]
    public void WindowsKnownFolderProvider_GetLocalApplicationDataPath_MatchesEnvironment()
    {
        // Act
        var localAppData = new WindowsKnownFolderProvider().GetLocalApplicationDataPath();

        // Assert
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), localAppData, ignoreCase: true);
    }
}
