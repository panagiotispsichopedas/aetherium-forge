using AetheriumForge.Core.Archives;

namespace AetheriumForge.Core.Tests.Archives;

/// <summary>
/// Integration tests over the real vanilla archives. They run only when the <c>AETHERIUM_SKYRIM_DATA</c>
/// environment variable points to a Skyrim Special Edition <c>Data</c> folder.
/// </summary>
public sealed class BsaReaderVanillaTests
{
    private const string DataFolderVariable = "AETHERIUM_SKYRIM_DATA";

    [Fact]
    public void FindHashMismatches_VanillaTextureArchives_ReturnsEmpty()
    {
        // Arrange
        string dataFolder = RequireDataFolder();
        string[] archives = Directory.GetFiles(dataFolder, "Skyrim - Textures*.bsa");
        Assert.NotEmpty(archives);

        // Act
        var mismatches = new Dictionary<string, int>();
        foreach (string archive in archives)
        {
            using BsaReader reader = BsaReader.Open(archive);
            int count = reader.FindHashMismatches().Count;
            if (count > 0)
            {
                mismatches[Path.GetFileName(archive)] = count;
            }
        }

        // Assert
        Assert.Empty(mismatches);
    }

    [Fact]
    public void ReadPrefix_FirstVanillaTexture_MatchesReadFileAndStartsWithDdsMagic()
    {
        // Arrange
        string archive = Path.Combine(RequireDataFolder(), "Skyrim - Textures0.bsa");
        Assert.SkipUnless(File.Exists(archive), $"'{archive}' not found.");
        using BsaReader reader = BsaReader.Open(archive);
        BsaEntry entry = reader.Entries[0];
        byte[] prefix = new byte[148];

        // Act
        int written = reader.ReadPrefix(entry, prefix);
        byte[] full = reader.ReadFile(entry);

        // Assert
        Assert.Equal(full.AsSpan(0, 148).ToArray(), prefix.AsSpan(0, written).ToArray());
        Assert.True(prefix.AsSpan(0, 4).SequenceEqual("DDS "u8));
    }

    private static string RequireDataFolder()
    {
        string? dataFolder = Environment.GetEnvironmentVariable(DataFolderVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dataFolder), $"{DataFolderVariable} is not set.");
        Assert.SkipUnless(Directory.Exists(dataFolder), $"{DataFolderVariable} '{dataFolder}' does not exist.");

        return dataFolder!;
    }
}
