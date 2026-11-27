using AetheriumForge.Core.Concurrency;

namespace AetheriumForge.Core.Tests.Concurrency;

public sealed class LockOwnerInfoTests
{
    [Fact]
    public void TryParse_SerializedRecord_RoundTrips()
    {
        var owner = new LockOwnerInfo(1234, "AetheriumForge", "Aetherium Forge", new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero), "a calibration");

        Assert.Equal(owner, LockOwnerInfo.TryParse(owner.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pid=abc\nowner=x\n")]
    [InlineData("pid=-5\n")]
    public void TryParse_Malformed_ReturnsNull(string? text)
    {
        Assert.Null(LockOwnerInfo.TryParse(text));
    }

    [Fact]
    public void ForCurrentProcess_DescriptionWithNewline_IsFlattened()
    {
        var owner = LockOwnerInfo.ForCurrentProcess("line one\nline two");

        Assert.Equal("line one line two", owner.Description);
    }

    [Fact]
    public void IsProcessAlive_CurrentProcess_IsTrue()
    {
        Assert.True(LockOwnerInfo.ForCurrentProcess("test").IsProcessAlive());
    }
}
