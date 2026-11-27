using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class PluginArchivesTests
{
    [Fact]
    public void For_ArchivesExist_ReturnsBsaThenTexturesInLoadOrder()
    {
        // Arrange
        string[] loadOrder = ["Skyrim.esm", "ModA.esp", "ModB.esl"];
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ModA.bsa", "ModA - Textures.bsa", "ModB - Textures.bsa",
        };

        // Act
        IReadOnlyList<string> result = PluginArchives.For(loadOrder, existing.Contains);

        // Assert
        Assert.Equal(["ModA.bsa", "ModA - Textures.bsa", "ModB - Textures.bsa"], result);
    }

    [Fact]
    public void For_NoArchivesExist_ReturnsEmpty()
    {
        // Act
        IReadOnlyList<string> result = PluginArchives.For(["ModA.esp"], _ => false);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void For_EntryWithoutPluginExtension_IsSkipped()
    {
        // Act
        IReadOnlyList<string> result = PluginArchives.For(["Readme.txt", "Mod.ESM"], _ => true);

        // Assert
        Assert.Equal(["Mod.bsa", "Mod - Textures.bsa"], result);
    }

    [Fact]
    public void For_NullLoadOrder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PluginArchives.For(null!, _ => true));
    }
}
