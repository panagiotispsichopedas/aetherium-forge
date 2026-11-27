using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class IniArchiveListTests
{
    private const string DefaultIni = "[General]\r\nsLanguage=ENGLISH\r\n\r\n[Archive]\r\n"
        + "sResourceArchiveList=Skyrim - Misc.bsa, Skyrim - Shaders.bsa, Skyrim - Interface.bsa\r\n"
        + "sResourceArchiveList2=Skyrim - Textures0.bsa, Skyrim - Textures1.bsa\r\n";

    [Fact]
    public void Resolve_SingleIni_ReturnsListThenList2()
    {
        // Act
        IReadOnlyList<string> result = IniArchiveList.Resolve([DefaultIni]);

        // Assert
        Assert.Equal(["Skyrim - Misc.bsa", "Skyrim - Shaders.bsa", "Skyrim - Interface.bsa", "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa"], result);
    }

    [Fact]
    public void Resolve_LaterIniOverridesOneKey_KeepsOtherKeyFromEarlier()
    {
        // Arrange
        const string customIni = "[archive]\nSRESOURCEARCHIVELIST2 = Skyrim - Textures0.bsa,Custom.bsa\n";

        // Act
        IReadOnlyList<string> result = IniArchiveList.Resolve([DefaultIni, string.Empty, customIni]);

        // Assert
        Assert.Equal(["Skyrim - Misc.bsa", "Skyrim - Shaders.bsa", "Skyrim - Interface.bsa", "Skyrim - Textures0.bsa", "Custom.bsa"], result);
    }

    [Fact]
    public void Resolve_KeyOutsideArchiveSection_IsIgnored()
    {
        // Arrange
        const string ini = "[General]\nsResourceArchiveList=Wrong.bsa\n[Archive]\n; sResourceArchiveList2=Commented.bsa\nsResourceArchiveList=Right.bsa\n";

        // Act
        IReadOnlyList<string> result = IniArchiveList.Resolve([ini]);

        // Assert
        Assert.Equal(["Right.bsa"], result);
    }

    [Fact]
    public void Resolve_MissingKeys_ReturnsEmpty()
    {
        // Arrange
        const string ini = "[Display]\niSize W=1920\n";

        // Act
        IReadOnlyList<string> result = IniArchiveList.Resolve([ini]);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_BlankEntriesAndDuplicates_AreRemoved()
    {
        // Arrange
        const string ini = "﻿[Archive]\nsResourceArchiveList=A.bsa, ,B.bsa,\nsResourceArchiveList2=a.BSA, C.bsa\n";

        // Act
        IReadOnlyList<string> result = IniArchiveList.Resolve([ini]);

        // Assert
        Assert.Equal(["A.bsa", "B.bsa", "C.bsa"], result);
    }

    [Fact]
    public void Resolve_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => IniArchiveList.Resolve(null!));
    }
}
