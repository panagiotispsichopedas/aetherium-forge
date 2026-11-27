using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class LookSettingsTests
{
    /// <summary>SHA-256 of the canonical neutral JSON (denoise left out), checked independently with Python's hashlib.</summary>
    private const string NeutralHash = "971652fd19d635bf";

    [Fact]
    public void GetStableHash_Neutral_IsPinned()
    {
        // Act
        string hash = LookSettings.Neutral.GetStableHash();

        // Assert: pinned so that a change to the canonical form (which would mark every finished job as changed) is deliberate.
        Assert.Equal(NeutralHash, hash);
    }

    [Fact]
    public void GetStableHash_EqualSettings_GiveEqualHashes()
    {
        // Arrange
        var a = new LookSettings { Saturation = 1.25, Sharpen = new SharpenSettings(0.5, 2.0) };
        var b = new LookSettings { Saturation = 1.25, Sharpen = new SharpenSettings(0.5, 2.0) };

        // Act
        var hashes = (a.GetStableHash(), b.GetStableHash());

        // Assert
        Assert.Equal(hashes.Item1, hashes.Item2);
    }

    [Fact]
    public void GetStableHash_DifferentSettings_GiveDifferentHashes()
    {
        // Act
        string changed = (LookSettings.Neutral with { Temperature = 0.1 }).GetStableHash();

        // Assert
        Assert.NotEqual(LookSettings.Neutral.GetStableHash(), changed);
    }

    [Fact]
    public void GetStableHash_NegativeZero_HashesLikeZero()
    {
        // Act
        string hash = (LookSettings.Neutral with { Brightness = -0.0 }).GetStableHash();

        // Assert
        Assert.Equal(LookSettings.Neutral.GetStableHash(), hash);
    }

    [Fact]
    public void GetStableHash_AnyLook_Is16LowercaseHexDigits()
    {
        // Act
        string hash = (LookSettings.Neutral with { Denoise = 0.5 }).GetStableHash();

        // Assert
        Assert.Matches("^[0-9a-f]{16}$", hash);
    }

    [Fact]
    public void IsNeutral_DefaultLook_IsTrue()
    {
        // Act
        bool neutral = new LookSettings().IsNeutral;

        // Assert
        Assert.True(neutral);
    }

    [Fact]
    public void GetStableHash_DiffuseKindAndNormalStrengthChanged_IsUnchanged()
    {
        // Act
        string changed = (LookSettings.Neutral with { NormalStrength = 2.0, SpecularScale = 0.5 }).GetStableHash(TextureKind.Diffuse);

        // Assert
        Assert.Equal(LookSettings.Neutral.GetStableHash(TextureKind.Diffuse), changed);
    }

    [Fact]
    public void GetStableHash_NormalKindAndNormalStrengthChanged_Changes()
    {
        // Act
        string changed = (LookSettings.Neutral with { NormalStrength = 2.0 }).GetStableHash(TextureKind.Normal);

        // Assert
        Assert.NotEqual(LookSettings.Neutral.GetStableHash(TextureKind.Normal), changed);
    }

    [Fact]
    public void GetStableHash_NormalKindAndGradingChanged_IsUnchanged()
    {
        // Act
        string changed = (LookSettings.Neutral with { Saturation = 0.2, Sharpen = new SharpenSettings(1, 1) }).GetStableHash(TextureKind.Normal);

        // Assert
        Assert.Equal(LookSettings.Neutral.GetStableHash(TextureKind.Normal), changed);
    }

    [Fact]
    public void GetStableHash_ParallaxKindAndAiStrengthChanged_IsUnchanged()
    {
        // Act
        string changed = (LookSettings.Neutral with { AiStrength = 0.3 }).GetStableHash(TextureKind.Parallax);

        // Assert
        Assert.Equal(LookSettings.Neutral.GetStableHash(TextureKind.Parallax), changed);
    }

    [Fact]
    public void GetStableHash_DenoiseChanged_IsUnchanged()
    {
        // Act
        var hashes = ((LookSettings.Neutral with { Denoise = 0.7 }).GetStableHash(), (LookSettings.Neutral with { Denoise = 0.7 }).GetStableHash(TextureKind.Diffuse));

        // Assert
        Assert.Equal((LookSettings.Neutral.GetStableHash(), LookSettings.Neutral.GetStableHash(TextureKind.Diffuse)), hashes);
    }
}
