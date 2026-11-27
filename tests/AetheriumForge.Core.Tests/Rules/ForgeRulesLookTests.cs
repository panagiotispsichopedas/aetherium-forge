using System.Globalization;
using System.Text.Json;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class ForgeRulesLookTests
{
    private const string OldRulesJson = """
        {
          "groups": [
            { "name": "Clutter", "match": ["*\\clutter\\*"], "cap": 2048, "model": "realesrgan-x4plus", "normalStrength": 1.4 },
            { "name": "Large", "model": "realesrgan-x4plus" }
          ]
        }
        """;

    private static string GroupJson(string look, string groupExtras = "") =>
        $$"""{ "groups": [ { "name": "G", "model": "m"{{groupExtras}}, "look": {{look}} } ] }""";

    [Fact]
    public void FromJson_OldRulesWithGroupNormalStrength_BecomesLookSettingOnlyNormalStrength()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Assert
        Assert.Equal(new LookOverrides { NormalStrength = 1.4 }, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_OldRulesWithoutNormalStrength_LookIsNull()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Assert
        Assert.Equal((null, (LookOverrides?)null), (rules.Look, rules.Groups[1].Look));
    }

    [Fact]
    public void FromJson_EmptyLook_SetsNothing()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(GroupJson("{}"));

        // Assert
        Assert.Equal(LookOverrides.Empty, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_PartialLook_SetsOnlyGivenControls()
    {
        // Arrange
        string json = GroupJson("""{ "saturation": 1.2, "sharpen": { "amount": 0.5 } }""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(new LookOverrides { Saturation = 1.2, SharpenAmount = 0.5 }, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_LookWithoutNormalStrength_TakesGroupNormalStrength()
    {
        // Arrange
        string json = GroupJson("""{ "gamma": 1.1 }""", """, "normalStrength": 1.6""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(new LookOverrides { Gamma = 1.1, NormalStrength = 1.6 }, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_LookAndGroupNormalStrength_LookWins()
    {
        // Arrange
        string json = GroupJson("""{ "normalStrength": 2.0 }""", """, "normalStrength": 1.6""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal((2.0, 2.0), (rules.Groups[0].Look!.NormalStrength!.Value, rules.Groups[0].NormalStrength));
    }

    [Fact]
    public void FromJson_GlobalLook_IsRead()
    {
        // Arrange
        const string json = """{ "look": { "contrast": 1.2, "specularScale": 0.8 }, "groups": [ { "name": "G", "model": "m" } ] }""";

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(new LookOverrides { Contrast = 1.2, SpecularScale = 0.8 }, rules.Look);
    }

    [Theory]
    [InlineData("""{ "aiStrength": 1.5 }""")]
    [InlineData("""{ "aiStrength": -0.1 }""")]
    [InlineData("""{ "sharpen": { "amount": 6 } }""")]
    [InlineData("""{ "sharpen": { "amount": 1, "radius": 0 } }""")]
    [InlineData("""{ "denoise": 2 }""")]
    [InlineData("""{ "brightness": -1.5 }""")]
    [InlineData("""{ "contrast": 0.2 }""")]
    [InlineData("""{ "gamma": 3 }""")]
    [InlineData("""{ "saturation": 2.5 }""")]
    [InlineData("""{ "temperature": 1.01 }""")]
    [InlineData("""{ "normalStrength": -1 }""")]
    [InlineData("""{ "specularScale": 2.5 }""")]
    public void FromJson_GroupLookValueOutOfRange_ThrowsJsonException(string look)
    {
        // Act
        var act = () => ForgeRules.FromJson(GroupJson(look));

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Theory]
    [InlineData("""{ "gamma": 3 }""")]
    [InlineData("""{ "sharpen": { "radius": 20 } }""")]
    public void FromJson_GlobalLookValueOutOfRange_ThrowsJsonException(string look)
    {
        // Arrange
        string json = $$"""{ "look": {{look}}, "groups": [ { "name": "G", "model": "m" } ] }""";

        // Act
        var act = () => ForgeRules.FromJson(json);

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(4.5)]
    public void FromJson_GroupNormalStrengthOutOfRangeWithoutLook_ThrowsJsonException(double strength)
    {
        // Arrange
        string json = $$"""{ "groups": [ { "name": "G", "model": "m", "normalStrength": {{strength.ToString(CultureInfo.InvariantCulture)}} } ] }""";

        // Act
        var act = () => ForgeRules.FromJson(json);

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public void ToJson_RulesWithGlobalAndGroupLooks_RoundTrips()
    {
        // Arrange
        var rules = new ForgeRules
        {
            Look = new LookOverrides { Contrast = 1.1, SharpenRadius = 2.0 },
            Groups =
            [
                new GroupRule
                {
                    Name = "G",
                    Model = "m",
                    NormalStrength = 1.8,
                    Look = new LookOverrides { AiStrength = 0.75, SharpenAmount = 0.6, Denoise = 0.3, Temperature = -0.25, NormalStrength = 1.8, SpecularScale = 0.7 },
                },
                new GroupRule { Name = "H", Model = "m" },
            ],
        };

        // Act
        ForgeRules roundTripped = ForgeRules.FromJson(rules.ToJson());

        // Assert
        Assert.Equal(rules, roundTripped);
    }

    [Fact]
    public void ToJson_Default_RoundTrips()
    {
        // Act
        ForgeRules roundTripped = ForgeRules.FromJson(ForgeRules.Default.ToJson());

        // Assert
        Assert.Equal(ForgeRules.Default, roundTripped);
    }

    [Fact]
    public void ToJson_OldRules_RoundTrips()
    {
        // Arrange
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Act
        ForgeRules roundTripped = ForgeRules.FromJson(rules.ToJson());

        // Assert
        Assert.Equal(rules, roundTripped);
    }

    [Fact]
    public void ToJson_GroupNormalStrengthAlias_IsWrittenInsideLookOnly()
    {
        // Arrange: the alias set in memory without a look, as older code builds groups.
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Model = "m", NormalStrength = 1.4 }] };

        // Act
        using JsonDocument document = JsonDocument.Parse(rules.ToJson());
        JsonElement group = document.RootElement.GetProperty("groups")[0];

        // Assert
        Assert.Equal((1.4, false), (group.GetProperty("look").GetProperty("normalStrength").GetDouble(), group.TryGetProperty("normalStrength", out _)));
    }

    [Fact]
    public void ToJson_PartialLook_OmitsUnsetControlsAndComputedProperties()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Model = "m", Look = new LookOverrides { SpecularScale = 1.5 } }] };

        // Act
        using JsonDocument document = JsonDocument.Parse(rules.ToJson());
        JsonElement look = document.RootElement.GetProperty("groups")[0].GetProperty("look");

        // Assert
        Assert.Equal(["specularScale"], look.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ToJson_NoGlobalLook_OmitsTopLevelLook()
    {
        // Act
        using JsonDocument document = JsonDocument.Parse(ForgeRules.Default.ToJson());

        // Assert
        Assert.False(document.RootElement.TryGetProperty("look", out _));
    }
}
