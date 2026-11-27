using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class PluginListParserTests
{
    [Fact]
    public void ParseActive_MixedLines_ReturnsOnlyStarredInFileOrder()
    {
        // Arrange
        const string content = "# This file is used by Skyrim to keep track of your downloaded content.\r\n"
            + "*Unofficial Skyrim Special Edition Patch.esp\r\n"
            + "\r\n"
            + "InactiveMod.esp\r\n"
            + "*SkyUI_SE.esp\r\n"
            + "#*CommentedOut.esp\r\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseActive(content);

        // Assert
        Assert.Equal(["Unofficial Skyrim Special Edition Patch.esp", "SkyUI_SE.esp"], result);
    }

    [Fact]
    public void ParseActive_ByteOrderMarkAndWhitespace_TrimsNames()
    {
        // Arrange
        const string content = "﻿*First.esp\n   *  Second.esm  \t\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseActive(content);

        // Assert
        Assert.Equal(["First.esp", "Second.esm"], result);
    }

    [Fact]
    public void ParseActive_DuplicateDifferentCase_KeepsFirst()
    {
        // Arrange
        const string content = "*Mod.esp\n*Other.esp\n*MOD.ESP\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseActive(content);

        // Assert
        Assert.Equal(["Mod.esp", "Other.esp"], result);
    }

    [Fact]
    public void ParseActive_EmptyContent_ReturnsEmpty()
    {
        // Act
        IReadOnlyList<string> result = PluginListParser.ParseActive(string.Empty);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ParseActive_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PluginListParser.ParseActive(null!));
    }

    [Fact]
    public void ParseCcc_LinesWithBlanks_ReturnsTrimmedEntries()
    {
        // Arrange
        const string content = "ccBGSSSE001-Fish.esm\r\n\r\n  ccQDRSSE001-SurvivalMode.esl \r\nccBGSSSE025-AdvDSGS.esm";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseCcc(content);

        // Assert
        Assert.Equal(["ccBGSSSE001-Fish.esm", "ccQDRSSE001-SurvivalMode.esl", "ccBGSSSE025-AdvDSGS.esm"], result);
    }

    [Fact]
    public void ParseCrashLoggerPlugins_CrashLoggerSseAeLog_ReturnsPluginsInOrder()
    {
        // Arrange
        const string log = "Skyrim SSE v1.6.1170\r\n"
            + "CrashLoggerSSE v1-15-0-0 Jul 10 2024 14:24:20\r\n"
            + "\r\n"
            + "Unhandled exception \"EXCEPTION_ACCESS_VIOLATION\" at 0x7FF6A1B2C3D4 SkyrimSE.exe+0123456\r\n"
            + "\r\n"
            + "SKSE PLUGINS:\r\n"
            + "\tCrashLogger.dll v1.15\r\n"
            + "\r\n"
            + "PLUGINS:\r\n"
            + "\tLight: 1\tRegular: 3\tTotal: 4\r\n"
            + "\t[00]     Skyrim.esm\r\n"
            + "\t[01]     Update.esm\r\n"
            + "\t[02]     Unofficial Skyrim Special Edition Patch.esp\r\n"
            + "\t[FE:000] ccBGSSSE001-Fish.esm\r\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseCrashLoggerPlugins(log);

        // Assert
        Assert.Equal(["Skyrim.esm", "Update.esm", "Unofficial Skyrim Special Edition Patch.esp", "ccBGSSSE001-Fish.esm"], result);
    }

    [Fact]
    public void ParseCrashLoggerPlugins_OlderFormatFollowedBySection_StopsAtNextHeader()
    {
        // Arrange
        const string log = "PLUGINS:\n"
            + "\t[ 0] Skyrim.esm\n"
            + "\t[ 1] Update.esm\n"
            + "MODULES:\n"
            + "\t[ 2] NotAPlugin.esp\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseCrashLoggerPlugins(log);

        // Assert
        Assert.Equal(["Skyrim.esm", "Update.esm"], result);
    }

    [Fact]
    public void ParseCrashLoggerPlugins_OnlySksePluginsSection_ReturnsEmpty()
    {
        // Arrange
        const string log = "SKSE PLUGINS:\n\t[00] Fake.esp\n";

        // Act
        IReadOnlyList<string> result = PluginListParser.ParseCrashLoggerPlugins(log);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ParseCrashLoggerPlugins_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PluginListParser.ParseCrashLoggerPlugins(null!));
    }
}
