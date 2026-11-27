using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Tests.Common;

public sealed class GamePathTests
{
    [Theory]
    [InlineData("Textures/Clutter/Bucket01.DDS", @"textures\clutter\bucket01.dds")]
    [InlineData(@"\textures\x.dds\", @"textures\x.dds")]
    [InlineData(@"  TEXTURES\A\B.dds ", @"textures\a\b.dds")]
    public void Normalize_MixedInput_ReturnsCanonicalPath(string input, string expected)
    {
        // Act
        var result = GamePath.Normalize(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GamePath.Normalize(null!));
    }
}
