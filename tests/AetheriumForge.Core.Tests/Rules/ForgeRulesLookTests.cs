using System.Text.Json;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class ForgeRulesLookTests
{
    private const string OldRulesJson = """
        {
          "groups": [
            { "name": "Clutter", "match": ["*\\clutter\\*"], "cap": 2048, "model": "realesrgan-x4plus", "normalStrength": 1.4 }
          ]
        }
        """;

    private static string GroupJson(string look, string groupExtras = "") =>
        $$"""{ "groups": [ { "name": "G", "model": "m"{{groupExtras}}, "look": {{look}} } ] }""";

    [Fact]
    public void FromJson_RulesWithoutLook_LookIsNull()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Assert
        Assert.Null(rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_RulesWithoutLook_EffectiveLookCarriesGroupNormalStrength()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Assert
        Assert.Equal(LookSettings.Neutral with { NormalStrength = 1.4 }, rules.Groups[0].EffectiveLook);
    }

    [Fact]
    public void FromJson_EmptyLook_KeepsNeutralDefaults()
    {
        // Act
        ForgeRules rules = ForgeRules.FromJson(GroupJson("{}"));

        // Assert
        Assert.Equal(LookSettings.Neutral, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_PartialLook_SetsGivenValuesAndKeepsOtherDefaults()
    {
        // Arrange
        string json = GroupJson("""{ "saturation": 1.2, "sharpen": { "amount": 0.5 } }""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(LookSettings.Neutral with { Saturation = 1.2, Sharpen = new SharpenSettings(0.5, 1.0) }, rules.Groups[0].Look);
    }

    [Fact]
    public void FromJson_LookWithoutNormalStrength_TakesGroupNormalStrength()
    {
        // Arrange
        string json = GroupJson("""{ "gamma": 1.1 }""", """, "normalStrength": 1.6""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(1.6, rules.Groups[0].Look!.NormalStrength);
    }

    [Fact]
    public void FromJson_LookAndGroupNormalStrength_LookWins()
    {
        // Arrange
        string json = GroupJson("""{ "normalStrength": 2.0 }""", """, "normalStrength": 1.6""");

        // Act
        ForgeRules rules = ForgeRules.FromJson(json);

        // Assert
        Assert.Equal(2.0, rules.Groups[0].EffectiveLook.NormalStrength);
    }

    [Theory]
    [InlineData("""{ "aiStrength": 1.5 }""")]
    [InlineData("""{ "aiStrength": -0.1 }""")]
    [InlineData("""{ "sharpen": { "amount": 6 } }""")]
    [InlineData("""{ "sharpen": { "amount": 1, "radius": 0 } }""")]
    [InlineData("""{ "sharpen": null }""")]
    [InlineData("""{ "denoise": 2 }""")]
    [InlineData("""{ "brightness": -1.5 }""")]
    [InlineData("""{ "contrast": 0.2 }""")]
    [InlineData("""{ "gamma": 3 }""")]
    [InlineData("""{ "saturation": 2.5 }""")]
    [InlineData("""{ "temperature": 1.01 }""")]
    [InlineData("""{ "normalStrength": -1 }""")]
    [InlineData("""{ "specularScale": 2.5 }""")]
    public void FromJson_LookValueOutOfRange_ThrowsJsonException(string look)
    {
        // Act
        var act = () => ForgeRules.FromJson(GroupJson(look));

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public void FromJson_GroupNormalStrengthOutOfLookRange_ThrowsJsonExceptionWhenLookPresent()
    {
        // Arrange: the alias flows into the look, which validates it.
        string json = GroupJson("{}", """, "normalStrength": 9""");

        // Act
        var act = () => ForgeRules.FromJson(json);

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public void ToJson_RulesWithLook_RoundTrips()
    {
        // Arrange
        var look = new LookSettings
        {
            AiStrength = 0.75,
            Sharpen = new SharpenSettings(0.6, 1.5),
            Denoise = 0.3,
            Brightness = 0.05,
            Contrast = 1.1,
            Gamma = 0.9,
            Saturation = 1.2,
            Temperature = -0.25,
            NormalStrength = 1.8,
            SpecularScale = 0.7,
        };
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Model = "m", NormalStrength = 1.8, Look = look }] };

        // Act
        ForgeRules roundTripped = ForgeRules.FromJson(rules.ToJson());

        // Assert
        Assert.Equal(rules, roundTripped);
    }

    [Fact]
    public void ToJson_OldRules_RoundTripsWithoutLook()
    {
        // Arrange
        ForgeRules rules = ForgeRules.FromJson(OldRulesJson);

        // Act
        string json = rules.ToJson();

        // Assert
        Assert.Equal((false, rules), (json.Contains("\"look\"", StringComparison.Ordinal), ForgeRules.FromJson(json)));
    }

    [Fact]
    public void ToJson_RulesWithLook_WritesCamelCaseLookWithoutComputedProperties()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Model = "m", Look = LookSettings.Neutral with { SpecularScale = 1.5 } }] };

        // Act
        using JsonDocument document = JsonDocument.Parse(rules.ToJson());
        JsonElement group = document.RootElement.GetProperty("groups")[0];

        // Assert
        Assert.Equal(
            (1.5, false, false, false),
            (group.GetProperty("look").GetProperty("specularScale").GetDouble(),
             group.GetProperty("look").TryGetProperty("isNeutral", out _),
             group.TryGetProperty("effectiveLook", out _),
             group.GetProperty("look").TryGetProperty("denoise", out _)));
    }
}
