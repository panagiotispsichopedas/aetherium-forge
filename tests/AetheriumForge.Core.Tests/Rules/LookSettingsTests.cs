using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class LookSettingsTests
{
    /// <summary>SHA-256 of the canonical neutral JSON, checked independently with Python's hashlib.</summary>
    private const string NeutralHash = "596d6f5ffb44681f";

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
}
