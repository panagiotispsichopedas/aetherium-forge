using AetheriumForge.Core.Archives;

namespace AetheriumForge.Core.Tests.Archives;

/// <summary>
/// Hash reference values. Each value's low 32 bits (h1) can be checked by hand from IMPLEMENTATION_GUIDE §6;
/// the full values are the hashes stored in the vanilla Skyrim SE archives named in each case, except
/// <c>a.kf</c>, which is derived by hand below (no vanilla archive contains a <c>.kf</c> file).
/// </summary>
public sealed class TesHashTests
{
    [Fact]
    public void HashFile_SingleCharRootKf_MatchesHandDerivedValue()
    {
        // Arrange
        // root "a" (len 1): h1 = 'a' | 1 << 16 | 'a' << 24 | 0x80 (.kf) = 0x610100E1; h2 = 0 (no middle chars).
        // h3 over ".kf": ((0x2E * 0x1003F + 0x6B) * 0x1003F + 0x66) mod 2^32 = 0x1711E3E9.
        const ulong expected = 0x1711E3E9_610100E1;

        // Act
        ulong hash = TesHash.HashFile("a.kf");

        // Assert
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("go.nif", 0x92CD45FD_6702806FUL)] // Skyrim - Meshes0.bsa, 2-char root: h1 = 'o' | 2 << 16 | 'g' << 24 | 0x8000.
    [InlineData("up.hkx", 0x8FDAE987_75020070UL)] // Skyrim - Animations.bsa, 2-char root, extension without flag bits.
    [InlineData("fox.dds", 0x8DDBA9C5_6603EFF8UL)] // Skyrim - Textures1.bsa: h1 = 'x' | 'o' << 8 | 3 << 16 | 'f' << 24 | 0x8080.
    [InlineData("key01.dds", 0x8E40C319_6B05B0B1UL)] // Skyrim - Textures3.bsa.
    [InlineData("ui_menu_ok.wav", 0x26BC4E48_F50A6F6BUL)] // Skyrim - Sounds.bsa: h1 includes 0x80000000 for .wav.
    [InlineData("tamriel.16.0.0.dds", 0xE4203730_740EAEB0UL)] // Skyrim - Textures6.bsa: only the last dot starts the extension.
    public void HashFile_VanillaName_MatchesStoredArchiveHash(string fileName, ulong expected)
    {
        // Act
        ulong hash = TesHash.HashFile(fileName);

        // Assert
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("meshes", 0x322F3A9A_6D066573UL)] // Skyrim - Meshes0.bsa: h1 = 's' | 'e' << 8 | 6 << 16 | 'm' << 24.
    [InlineData("textures", 0xD507789E_74086573UL)] // Skyrim - Textures0.bsa.
    [InlineData(@"textures\clutter", 0x048BF47C_74106572UL)] // Skyrim - Textures3.bsa.
    [InlineData(@"textures\actors\fox", 0x007792B8_74136F78UL)] // Skyrim - Textures1.bsa.
    public void HashFolder_VanillaFolder_MatchesStoredArchiveHash(string folderPath, ulong expected)
    {
        // Act
        ulong hash = TesHash.HashFolder(folderPath);

        // Assert
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void HashFile_MixedCase_EqualsLowercaseHash()
    {
        // Act
        ulong hash = TesHash.HashFile("Key01.DDS");

        // Assert
        Assert.Equal(TesHash.HashFile("key01.dds"), hash);
    }

    [Fact]
    public void HashFolder_ForwardSlashesAndMixedCase_EqualsCanonicalHash()
    {
        // Act
        ulong hash = TesHash.HashFolder("Textures/Clutter");

        // Assert
        Assert.Equal(0x048BF47C_74106572UL, hash);
    }

    [Fact]
    public void HashFile_NoExtension_HashesWholeNameAsRoot()
    {
        // Act
        ulong hash = TesHash.HashFile("meshes");

        // Assert
        Assert.Equal(TesHash.HashFolder("meshes"), hash);
    }

    [Fact]
    public void HashFile_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TesHash.HashFile(null!));
    }

    [Fact]
    public void HashFolder_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TesHash.HashFolder(null!));
    }
}
