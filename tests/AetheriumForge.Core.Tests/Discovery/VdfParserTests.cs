using AetheriumForge.Core.Discovery;

namespace AetheriumForge.Core.Tests.Discovery;

public sealed class VdfParserTests
{
    [Fact]
    public void Parse_NestedBlocks_BuildsTree()
    {
        // Arrange
        const string text = """
            "AppState"
            {
                "appid"     "489830"
                "UserConfig"
                {
                    "language"  "english"
                }
            }
            """;

        // Act
        var root = VdfParser.Parse(text);

        // Assert
        Assert.Equal("english", root["AppState"]?["UserConfig"]?["language"]?.Value);
    }

    [Fact]
    public void Parse_KeyLookup_IsCaseInsensitive()
    {
        // Arrange
        const string text = "\"AppState\" { \"installdir\" \"Skyrim Special Edition\" }";

        // Act
        var root = VdfParser.Parse(text);

        // Assert
        Assert.Equal("Skyrim Special Edition", root["appstate"]?["INSTALLDIR"]?.Value);
    }

    [Theory]
    [InlineData(@"""k"" ""D:\\SteamLibrary""", @"D:\SteamLibrary")]
    [InlineData(@"""k"" ""say \""hi\""""", "say \"hi\"")]
    [InlineData(@"""k"" ""a\tb\nc""", "a\tb\nc")]
    [InlineData(@"""k"" ""keep\q""", @"keep\q")]
    public void Parse_EscapedValue_Unescapes(string text, string expected)
    {
        // Act
        var root = VdfParser.Parse(text);

        // Assert
        Assert.Equal(expected, root["k"]?.Value);
    }

    [Fact]
    public void Parse_MultipleLibraries_ReturnsEveryLibraryInOrder()
    {
        // Arrange
        const string text = """
            "libraryfolders"
            {
                "0"
                {
                    "path"  "C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "123" }
                }
                "1"
                {
                    "path"  "D:\\SteamLibrary"
                    "apps" { "489830" "45678" }
                }
                "2"
                {
                    "path"  "E:\\Games\\Steam"
                    "apps" { }
                }
            }
            """;

        // Act
        var paths = VdfParser.Parse(text)["libraryfolders"]!.Children.Select(library => library["path"]?.Value).ToList();

        // Assert
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam"], paths);
    }

    [Fact]
    public void Parse_CommentsConditionalsAndUnquotedTokens_AreHandled()
    {
        // Arrange
        const string text = """
            // header comment
            root
            {
                key value [$WIN32]
                "other" "x" // trailing comment
            }
            """;

        // Act
        var root = VdfParser.Parse(text)["root"]!;

        // Assert
        Assert.Equal(["value", "x"], root.Children.Select(child => child.Value));
    }

    [Fact]
    public void Parse_EmptyBlock_HasNoChildrenAndNoValue()
    {
        // Act
        var node = VdfParser.Parse("\"apps\" { }")["apps"]!;

        // Assert
        Assert.True(node.Value is null && node.Children.Count == 0);
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("}")]
    [InlineData("\"a\"")]
    [InlineData("{ }")]
    public void Parse_MalformedText_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => VdfParser.Parse(text));
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VdfParser.Parse(null!));
    }
}
