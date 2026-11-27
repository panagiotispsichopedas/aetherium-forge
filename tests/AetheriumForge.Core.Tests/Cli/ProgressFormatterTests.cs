using AetheriumForge.Cli.Rendering;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class ProgressFormatterTests
{
    private static PipelineProgress Snapshot(TimeSpan? eta = null, string? error = null) => new(
        PipelineState.Running,
        Done: 25,
        Failed: 1,
        Remaining: 74,
        Total: 100,
        Stages:
        [
            new StageActivity(PipelineStage.Decode, @"textures\clutter\bucket01.dds", 1, 3, 0, 0),
            new StageActivity(PipelineStage.Upscale, @"textures\architecture\whiterun\wrwoodplank01.dds", 1, 0, 12, 36),
            new StageActivity(PipelineStage.PostProcess, null, 0, 0, 0, 0),
            new StageActivity(PipelineStage.Encode, null, 0, 0, 0, 0),
        ],
        SourceMegapixelsPerSecond: 1.234,
        Eta: eta,
        Elapsed: TimeSpan.FromSeconds(3725),
        LastError: error);

    [Fact]
    public void FormatLive_Snapshot_ReturnsFixedLineCount()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(), 120);

        Assert.Equal(ProgressFormatter.LiveLineCount, lines.Count);
    }

    [Fact]
    public void FormatLive_NarrowConsole_NoLineExceedsWidth()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(error: new string('x', 500)), 60);

        Assert.All(lines, line => Assert.True(line.Length <= 59, $"'{line}' is {line.Length} characters"));
    }

    [Fact]
    public void FormatLive_Header_ShowsStateAndCounts()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(), 120);

        Assert.Equal("Aetherium Forge · Running · 25 done · 1 failed · 74 remaining", lines[0]);
    }

    [Fact]
    public void FormatLive_UpscaleStage_ShowsPathAndTiles()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(), 120);

        Assert.Equal(@"  Upscale     textures\architecture\whiterun\wrwoodplank01.dds  tiles 12/36", lines[2]);
    }

    [Fact]
    public void FormatLive_IdleStage_ShowsIdle()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(), 120);

        Assert.Equal("  PostProcess idle", lines[3]);
    }

    [Fact]
    public void FormatLive_Bar_ShowsPercentAndCounter()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(), 80);

        Assert.EndsWith(" 26.0%  26/100", lines[5], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLive_Throughput_ShowsRateElapsedAndEta()
    {
        var lines = ProgressFormatter.FormatLive(Snapshot(eta: TimeSpan.FromMinutes(192)), 120);

        Assert.Equal("1.23 MP/s · elapsed 1:02:05 · ETA 3h 12m", lines[6]);
    }

    [Fact]
    public void FormatPlain_Snapshot_IsOneSelfContainedLine()
    {
        var line = ProgressFormatter.FormatPlain(Snapshot());

        Assert.Equal(
            @"[Running] 26/100 (done 25, failed 1, remaining 74) 1.23 MP/s, elapsed 1:02:05, ETA -- | Decode: textures\clutter\bucket01.dds | Upscale: textures\architecture\whiterun\wrwoodplank01.dds (12/36 tiles)",
            line);
    }

    [Theory]
    [InlineData(null, "--")]
    [InlineData(12.0, "12s")]
    [InlineData(245.0, "4m 05s")]
    [InlineData(11520.0, "3h 12m")]
    [InlineData(93600.0, "1d 2h")]
    public void FormatEta_Durations_AreCompact(double? seconds, string expected)
    {
        var text = ProgressFormatter.FormatEta(seconds is { } s ? TimeSpan.FromSeconds(s) : null);

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(0.0, "░░░░░░░░░░")]
    [InlineData(0.5, "█████░░░░░")]
    [InlineData(1.5, "██████████")]
    public void Bar_Fraction_FillsProportionally(double fraction, string expected)
    {
        var bar = ProgressFormatter.Bar(fraction, 10);

        Assert.Equal(expected, bar);
    }

    [Fact]
    public void TruncateStart_LongPath_KeepsFileName()
    {
        var text = ProgressFormatter.TruncateStart(@"textures\architecture\whiterun\wrwoodplank01.dds", 20);

        Assert.Equal(@"…n\wrwoodplank01.dds", text);
    }
}
