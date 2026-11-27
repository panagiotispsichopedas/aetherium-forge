using System.Text.Json;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class LookScopesTests
{
    private static readonly ForgeRules Rules = new()
    {
        Look = new LookOverrides { Contrast = 1.2, Saturation = 1.1, SpecularScale = 0.9 },
        Groups =
        [
            new GroupRule { Name = "Armor", Model = "m", Look = new LookOverrides { Saturation = 1.5, NormalStrength = 1.6 } },
            new GroupRule { Name = "Legacy", Model = "m", NormalStrength = 1.3 },
            new GroupRule { Name = "Plain", Model = "m" },
        ],
    };

    [Fact]
    public void Resolve_NoScopes_IsNeutral()
    {
        // Act
        LookSettings look = LookResolver.ResolveScopes(null, null, null);

        // Assert
        Assert.Equal(LookSettings.Neutral, look);
    }

    [Fact]
    public void Resolve_AllScopes_EachControlFromNearestScope()
    {
        // Arrange
        var texture = new LookOverrides { Saturation = 0.5, Gamma = 1.4 };

        // Act
        LookSettings look = LookResolver.Resolve(Rules, "armor", texture);

        // Assert: saturation and gamma from the texture, normal strength from the group, contrast and specular from All, the rest neutral.
        Assert.Equal(
            LookSettings.Neutral with { Saturation = 0.5, Gamma = 1.4, NormalStrength = 1.6, Contrast = 1.2, SpecularScale = 0.9 },
            look);
    }

    [Fact]
    public void Resolve_UnknownGroup_UsesGlobalAndTextureOnly()
    {
        // Act
        LookSettings look = LookResolver.Resolve(Rules, "gone", new LookOverrides { AiStrength = 0.5 });

        // Assert
        Assert.Equal(LookSettings.Neutral with { AiStrength = 0.5, Contrast = 1.2, Saturation = 1.1, SpecularScale = 0.9 }, look);
    }

    [Fact]
    public void Resolve_GroupWithLegacyNormalStrength_UsesAlias()
    {
        // Act
        LookSettings look = LookResolver.Resolve(Rules, "Legacy", null);

        // Assert
        Assert.Equal(1.3, look.NormalStrength);
    }

    [Fact]
    public void Resolve_GroupWithoutLook_InheritsGlobal()
    {
        // Act
        LookSettings look = LookResolver.Resolve(Rules, "Plain", null);

        // Assert
        Assert.Equal(LookSettings.Neutral with { Contrast = 1.2, Saturation = 1.1, SpecularScale = 0.9 }, look);
    }

    [Fact]
    public void Resolve_SharpenAmountAndRadiusFromDifferentScopes_AreCombined()
    {
        // Act
        LookSettings look = LookResolver.ResolveScopes(new LookOverrides { SharpenRadius = 2.5 }, new LookOverrides { SharpenAmount = 0.8 }, null);

        // Assert
        Assert.Equal(new SharpenSettings(0.8, 2.5), look.Sharpen);
    }

    [Fact]
    public void From_FullSettings_ResolvesBackToTheSameSettings()
    {
        // Arrange
        var settings = new LookSettings { AiStrength = 0.4, Sharpen = new SharpenSettings(1, 2), Denoise = 0.2, Temperature = 0.3, SpecularScale = 1.7 };

        // Act
        LookSettings resolved = LookOverrides.From(settings).Resolve(LookSettings.Neutral with { Contrast = 1.9 });

        // Assert
        Assert.Equal(settings, resolved);
    }

    [Fact]
    public void Over_UnsetControls_ComeFromParent()
    {
        // Act
        LookOverrides combined = new LookOverrides { Gamma = 1.2 }.Over(new LookOverrides { Gamma = 0.8, Brightness = 0.1 });

        // Assert
        Assert.Equal(new LookOverrides { Gamma = 1.2, Brightness = 0.1 }, combined);
    }

    [Fact]
    public void ToJson_PartialOverride_RoundTripsThroughFromJson()
    {
        // Arrange
        var look = new LookOverrides { SharpenRadius = 1.5, Denoise = 0.25, NormalStrength = 2.0 };

        // Act
        LookOverrides roundTripped = LookOverrides.FromJson(look.ToJson());

        // Assert
        Assert.Equal(look, roundTripped);
    }

    [Fact]
    public void ToJson_PartialOverride_IsCompactAndOmitsUnset()
    {
        // Act
        string json = new LookOverrides { Saturation = 1.2 }.ToJson();

        // Assert
        Assert.Equal("""{"saturation":1.2}""", json);
    }

    [Fact]
    public void FromJson_OutOfRange_ThrowsJsonException()
    {
        // Act
        var act = () => LookOverrides.FromJson("""{ "contrast": 9 }""");

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public void IsEmpty_NewOverride_IsTrue()
    {
        // Act
        bool empty = new LookOverrides().IsEmpty;

        // Assert
        Assert.True(empty);
    }
}
