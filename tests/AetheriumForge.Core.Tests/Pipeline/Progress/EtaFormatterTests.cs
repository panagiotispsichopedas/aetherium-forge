using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline.Progress;

public sealed class EtaFormatterTests
{
    [Theory]
    [InlineData(0, "~0 s")]
    [InlineData(45, "~45 s")]
    [InlineData(59.4, "~59 s")]
    [InlineData(59.6, "~1 min")]
    [InlineData(200, "~3 min 20 s")]
    [InlineData(3599.6, "~1 h")]
    [InlineData(8100, "~2 h 15 min")]
    [InlineData(86_380, "~1 d")]
    [InlineData(100_800, "~1 d 4 h")]
    public void Format_Duration_UsesAtMostTwoUnits(double seconds, string expected) =>
        Assert.Equal(expected, EtaFormatter.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Format_Null_ReturnsUnknown() => Assert.Equal("unknown", EtaFormatter.Format(null));

    [Fact]
    public void Format_Negative_ReturnsUnknown() => Assert.Equal("unknown", EtaFormatter.Format(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void Format_MaxValue_DoesNotThrow() => Assert.StartsWith("~", EtaFormatter.Format(TimeSpan.MaxValue), StringComparison.Ordinal);
}
