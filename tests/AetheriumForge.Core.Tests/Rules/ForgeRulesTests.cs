using System.Text.Json;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class ForgeRulesTests
{
    [Fact]
    public void Default_Groups_AreClutterThenLarge()
    {
        // Act
        IReadOnlyList<GroupRule> groups = ForgeRules.Default.Groups;

        // Assert
        Assert.Equal(["Clutter", "Large"], groups.Select(g => g.Name));
    }

    [Fact]
    public void Default_ClutterGroup_MatchesGuideValues()
    {
        // Arrange
        var expected = new GroupRule { Name = "Clutter", Match = [@"*\clutter\*"], Cap = 2048, MaxFactor = 4, Model = "realesrgan-x4plus", NormalStrength = 1.4 };

        // Act
        GroupRule clutter = ForgeRules.Default.Groups[0];

        // Assert
        Assert.Equal(expected, clutter);
    }

    [Fact]
    public void Default_MinSource_Is128()
    {
        Assert.Equal(128, ForgeRules.Default.MinSource);
    }

    [Fact]
    public void Default_Exclude_ContainsGuideFolders()
    {
        // Arrange
        string[] expected =
        [
            @"textures\actors\character\", @"textures\interface\", @"textures\lod\", @"textures\terrain\", @"textures\effects\",
            @"textures\cubemaps\", @"textures\dyndolod\", @"textures\test\", @"textures\shadertests\",
        ];

        // Act
        IReadOnlyList<string> exclude = ForgeRules.Default.Exclude;

        // Assert
        Assert.Equal(expected, exclude);
    }

    [Fact]
    public void ToJson_Default_RoundTripsToEqualRules()
    {
        // Arrange
        string json = ForgeRules.Default.ToJson();

        // Act
        ForgeRules restored = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(ForgeRules.Default, restored);
    }

    [Fact]
    public void ToJson_Default_UsesCamelCaseNames()
    {
        // Act
        string json = ForgeRules.Default.ToJson();

        // Assert
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement group = doc.RootElement.GetProperty("groups")[0];
        Assert.True(doc.RootElement.TryGetProperty("minSource", out _) && doc.RootElement.TryGetProperty("exclude", out _)
            && group.TryGetProperty("maxFactor", out _) && group.TryGetProperty("normalStrength", out _) && !group.TryGetProperty("kinds", out _));
    }

    [Fact]
    public void ToJson_Kinds_WrittenAsStrings()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "Normals", Model = "m", Kinds = [TextureKind.Normal] }] };

        // Act
        string json = rules.ToJson();

        // Assert
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("groups")[0].GetProperty("kinds")[0].ValueKind);
    }

    [Fact]
    public void FromJson_CustomRules_ReadsAllProperties()
    {
        // Arrange
        const string Json = """
            {
              // comments and trailing commas are allowed
              "groups": [
                { "name": "Small", "match": ["textures\\armor\\*"], "minSourceMax": 64, "maxSourceMax": 512,
                  "kinds": ["Diffuse", "normal"], "cap": 1024, "maxFactor": 2, "model": "x2", "normalStrength": 1.2, },
              ],
              "minSource": 64,
              "exclude": ["textures\\foo\\"],
            }
            """;
        var expected = new ForgeRules
        {
            Groups =
            [
                new GroupRule
                {
                    Name = "Small", Match = [@"textures\armor\*"], MinSourceMax = 64, MaxSourceMax = 512, Kinds = [TextureKind.Diffuse, TextureKind.Normal],
                    Cap = 1024, MaxFactor = 2, Model = "x2", NormalStrength = 1.2,
                },
            ],
            MinSource = 64,
            Exclude = [@"textures\foo\"],
        };

        // Act
        ForgeRules rules = ForgeRules.FromJson(Json);

        // Assert
        Assert.Equal(expected, rules);
    }

    [Fact]
    public void FromJson_OmittedProperties_KeepDefaults()
    {
        // Arrange
        const string Json = """{ "groups": [ { "name": "All", "model": "m" } ] }""";

        // Act
        ForgeRules rules = ForgeRules.FromJson(Json);

        // Assert
        Assert.Equal(new GroupRule { Name = "All", Model = "m", Match = ["*"], Cap = 4096, MaxFactor = 4, NormalStrength = 1.0 }, rules.Groups[0]);
        Assert.Equal(ForgeRules.DefaultExcludedFolders, rules.Exclude);
        Assert.Equal(128, rules.MinSource);
    }

    [Theory]
    [InlineData("""{ "groups": [ { "model": "m" } ] }""")]
    [InlineData("""{ "groups": [ { "name": "A" } ] }""")]
    [InlineData("""{ "groups": [ { "name": "A", "model": "m", "cap": 0 } ] }""")]
    [InlineData("""{ "groups": [ { "name": "A", "model": "m", "maxFactor": 0 } ] }""")]
    [InlineData("""{ "groups": [ { "name": "A", "model": "m", "match": [] } ] }""")]
    [InlineData("""{ "groups": [ { "name": "A", "model": "m", "minSourceMax": 600, "maxSourceMax": 500 } ] }""")]
    [InlineData("""{ "minSource": -1 }""")]
    [InlineData("""{ "groups": null }""")]
    [InlineData("""{ "exclude": null }""")]
    [InlineData("""{ "groups": [ { "name": "A", "model": "m", "kinds": ["Bogus"] } ] }""")]
    [InlineData("null")]
    [InlineData("{ not json")]
    public void FromJson_InvalidRules_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ForgeRules.FromJson(json));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsDefault()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), $"af-rules-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "nested", "rules.json");
        try
        {
            // Act
            await ForgeRules.Default.SaveAsync(path, TestContext.Current.CancellationToken);
            ForgeRules loaded = await ForgeRules.LoadAsync(path, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(ForgeRules.Default, loaded);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(@"textures\actors\character\male\body_n.dds", true)]
    [InlineData(@"Textures\Interface\Icons\x.dds", true)]
    [InlineData("textures/lod/tree.dds", true)]
    [InlineData(@"textures\terrain\tamriel\x.dds", true)]
    [InlineData(@"textures\effects\fx.dds", true)]
    [InlineData(@"textures\cubemaps\sky.dds", true)]
    [InlineData(@"textures\dyndolod\lod.dds", true)]
    [InlineData(@"textures\test\a.dds", true)]
    [InlineData(@"textures\shadertests\a.dds", true)]
    [InlineData(@"textures\actors\dragon\dragon.dds", false)]
    [InlineData(@"textures\testing\a.dds", false)]
    [InlineData(@"textures\lodge\wall.dds", false)]
    [InlineData(@"textures\clutter\bucket01.dds", false)]
    [InlineData(@"textures\interface", false)]
    public void IsExcluded_DefaultRules_MatchesFolderPrefixes(string path, bool expected)
    {
        // Act
        bool excluded = ForgeRules.Default.IsExcluded(path);

        // Assert
        Assert.Equal(expected, excluded);
    }

    [Fact]
    public void IsExcluded_PrefixWithoutTrailingSeparator_StillMatchesWholeFolder()
    {
        // Arrange
        var rules = new ForgeRules { Exclude = ["Textures/Mods"] };

        // Act
        bool inside = rules.IsExcluded(@"textures\mods\a.dds");
        bool sibling = rules.IsExcluded(@"textures\modsextra\a.dds");

        // Assert
        Assert.True(inside && !sibling);
    }

    [Fact]
    public void IsExcluded_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ForgeRules.Default.IsExcluded(null!));
    }
}
