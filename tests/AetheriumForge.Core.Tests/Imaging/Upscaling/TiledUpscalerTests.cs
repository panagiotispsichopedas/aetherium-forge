using System.Reflection;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using Microsoft.ML.OnnxRuntime;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

public sealed class TiledUpscalerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TilingOptions Tiling(int tileSize = 64, int overlap = 8, int minTileSize = 16, bool feather = false) =>
        new() { TileSize = tileSize, Overlap = overlap, MinTileSize = minTileSize, Feather = feather };

    private static float[] Clamped(PlanarImage image) => [.. image.Data.Select(value => Math.Clamp(value, 0f, 1f))];

    [Theory]
    [InlineData(64, 64)]
    [InlineData(200, 130)]
    [InlineData(129, 65)]
    [InlineData(1, 1)]
    [InlineData(3, 250)]
    [InlineData(257, 17)]
    public async Task UpscaleAsync_PointwiseModel_MatchesWholeImageExactly(int width, int height)
    {
        // Arrange
        var runner = new FakeModelRunner(4);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling());
        var source = FakeModelRunner.Pattern(width, height);
        var expected = FakeModelRunner.Apply(source, 4, value => value);

        // Act
        var actual = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(expected.Data, actual.Data);
    }

    [Theory]
    [InlineData(200, 130)]
    [InlineData(65, 300)]
    public async Task UpscaleAsync_FeatherWithPointwiseModel_MatchesWholeImage(int width, int height)
    {
        // Arrange
        var runner = new FakeModelRunner(2);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(feather: true));
        var source = FakeModelRunner.Pattern(width, height);
        var expected = FakeModelRunner.Apply(source, 2, value => value);

        // Act
        var actual = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.All(expected.Data.Zip(actual.Data), pair => Assert.Equal(pair.First, pair.Second, 1e-5f));
    }

    [Fact]
    public async Task UpscaleAsync_ModelOutputOutOfRange_ClampsToUnitRange()
    {
        // Arrange
        Func<float, float> function = value => (value * 1.5f) - 0.2f;
        var runner = new FakeModelRunner(2, function);
        using var upscaler = new TiledUpscaler(runner, "contrast", Tiling());
        var source = FakeModelRunner.Pattern(150, 90);
        var expected = Clamped(FakeModelRunner.Apply(source, 2, function));

        // Act
        var actual = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(expected, actual.Data);
    }

    [Fact]
    public async Task UpscaleAsync_ModelReturnsNaN_WritesZero()
    {
        // Arrange
        var runner = new FakeModelRunner(2, _ => float.NaN);
        using var upscaler = new TiledUpscaler(runner, "nan", Tiling());

        // Act
        var actual = await upscaler.UpscaleAsync(FakeModelRunner.Pattern(10, 10), null, PauseToken.None, Ct);

        // Assert
        Assert.All(actual.Data, value => Assert.Equal(0f, value));
    }

    [Fact]
    public async Task UpscaleAsync_ImageSmallerThanTile_RunsSingleTileWithoutOverlap()
    {
        // Arrange
        var runner = new FakeModelRunner(4);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64));

        // Act
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(40, 64), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal([(40, 64)], runner.Calls);
    }

    [Fact]
    public async Task UpscaleAsync_LargerImage_FeedsTilesWithOverlap()
    {
        // Arrange: 128 px splits into cores 64 + 64; each tile adds 8 px of overlap at the seam.
        var runner = new FakeModelRunner(4);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64, overlap: 8));

        // Act
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(128, 40), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal([(72, 40), (72, 40)], runner.Calls);
    }

    [Fact]
    public async Task UpscaleAsync_MultipleTiles_ReportsEveryTile()
    {
        // Arrange: 3 × 2 tiles.
        var runner = new FakeModelRunner(2);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64));
        var progress = new SynchronousProgress();

        // Act
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(150, 100), progress, PauseToken.None, Ct);

        // Assert
        Assert.Equal(
            [new TileProgress(0, 6), new TileProgress(1, 6), new TileProgress(2, 6), new TileProgress(3, 6), new TileProgress(4, 6), new TileProgress(5, 6), new TileProgress(6, 6)],
            progress.Reports);
    }

    [Fact]
    public async Task UpscaleAsync_PausedAfterFirstTile_WaitsUntilResumed()
    {
        // Arrange
        var pause = new PauseTokenSource();
        var firstTileDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeModelRunner(2)
        {
            BeforeRun = (call, _) =>
            {
                if (call == 1)
                {
                    pause.Pause();
                    firstTileDone.SetResult();
                }
            },
        };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64));
        var source = FakeModelRunner.Pattern(150, 100);

        // Act
        var upscale = upscaler.UpscaleAsync(source, null, pause.Token, Ct);
        await firstTileDone.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(200, Ct);
        var callsWhilePaused = runner.Calls.Count;
        var completedWhilePaused = upscale.IsCompleted;
        pause.Resume();
        var result = await upscale.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        Assert.Equal((1, false, 6), (callsWhilePaused, completedWhilePaused, runner.Calls.Count));
        Assert.Equal(FakeModelRunner.Apply(source, 2, value => value).Data, result.Data);
    }

    [Fact]
    public async Task UpscaleAsync_CancelledDuringTile_StopsBeforeNextTile()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runner = new FakeModelRunner(2) { BeforeRun = (_, _) => cancellation.Cancel() };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64));
        PlanarImage? result = null;

        // Act
        var exception = await Record.ExceptionAsync(async () => result = await upscaler.UpscaleAsync(FakeModelRunner.Pattern(150, 100), null, PauseToken.None, cancellation.Token));

        // Assert
        Assert.Equal((true, 1, true), (exception is OperationCanceledException, runner.Calls.Count, result is null));
    }

    [Fact]
    public async Task UpscaleAsync_CancelledWhilePaused_Throws()
    {
        // Arrange
        var pause = new PauseTokenSource();
        pause.Pause();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runner = new FakeModelRunner(2);
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling());
        var upscale = upscaler.UpscaleAsync(FakeModelRunner.Pattern(100, 100), null, pause.Token, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upscale.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task UpscaleAsync_OutOfMemoryAboveThreshold_HalvesTileSizeAndMatchesWholeImage()
    {
        // Arrange: tiles wider than 48 input pixels fail; 128 → 64 → 32 succeeds (32 + 2 × 8 overlap = 48).
        var runner = new FakeModelRunner(2) { FailWhen = tile => tile.Width > 48 || tile.Height > 48 };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 128, overlap: 8, minTileSize: 16));
        var source = FakeModelRunner.Pattern(200, 120);

        // Act
        var result = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(FakeModelRunner.Apply(source, 2, value => value).Data, result.Data);
    }

    [Fact]
    public async Task UpscaleAsync_OutOfMemory_KeepsReducedTileSizeForLaterImages()
    {
        // Arrange
        var runner = new FakeModelRunner(2) { FailWhen = tile => tile.Width > 48 || tile.Height > 48 };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 128, overlap: 8, minTileSize: 16));
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(200, 120), null, PauseToken.None, Ct);
        runner.Calls.Clear();

        // Act
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(100, 30), null, PauseToken.None, Ct);

        // Assert: no failed attempt at the old size; 100 px at tile 32 = 4 columns, 1 row.
        Assert.Equal((32, 4), (upscaler.CurrentTileSize, runner.Calls.Count));
    }

    [Fact]
    public async Task UpscaleAsync_OutOfMemoryAtMinimum_Rethrows()
    {
        // Arrange
        var runner = new FakeModelRunner(2) { FailWhen = _ => true };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64, minTileSize: 16));

        // Act
        var exception = await Record.ExceptionAsync(() => upscaler.UpscaleAsync(FakeModelRunner.Pattern(100, 100), null, PauseToken.None, Ct));

        // Assert: tried 64, 32 and 16, then gave up.
        Assert.Equal((true, 16, 3), (exception is VideoMemoryExhaustedException, upscaler.CurrentTileSize, runner.Calls.Count));
    }

    [Fact]
    public async Task UpscaleAsync_OnnxRuntimeOutOfMemoryMessage_HalvesTileSize()
    {
        // Arrange: the message DirectML reports for E_OUTOFMEMORY.
        var runner = new FakeModelRunner(2)
        {
            FailWhen = tile => tile.Width > 48,
            ThrowFactory = () => CreateOnnxRuntimeException(
                "[ErrorCode:RuntimeException] Non-zero status code returned while running Conv node. Status Message: DmlCommittedResourceAllocator.cpp(22)\\onnxruntime.dll!00007FF8: (caller: 00007FF8) Exception(3) tid(1f24) 8007000E Not enough memory resources are available to complete this operation."),
        };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64, overlap: 0, minTileSize: 16));

        // Act
        await upscaler.UpscaleAsync(FakeModelRunner.Pattern(64, 20), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(32, upscaler.CurrentTileSize);
    }

    [Fact]
    public async Task UpscaleAsync_OtherRuntimeError_RethrowsWithoutRetry()
    {
        // Arrange
        var runner = new FakeModelRunner(2)
        {
            FailWhen = _ => true,
            ThrowFactory = () => CreateOnnxRuntimeException("[ErrorCode:InvalidArgument] Got invalid dimensions for input"),
        };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 64));

        // Act
        var exception = await Record.ExceptionAsync(() => upscaler.UpscaleAsync(FakeModelRunner.Pattern(100, 100), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((true, 64, 1), (exception is OnnxRuntimeException, upscaler.CurrentTileSize, runner.Calls.Count));
    }

    [Fact]
    public async Task UpscaleAsync_ConcurrentCalls_SerialiseRunnerAccess()
    {
        // Arrange
        var runner = new FakeModelRunner(2) { BeforeRun = (_, _) => Thread.Sleep(2) };
        using var upscaler = new TiledUpscaler(runner, "nearest", Tiling(tileSize: 32));

        // Act
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => upscaler.UpscaleAsync(FakeModelRunner.Pattern(100, 70), null, PauseToken.None, Ct)));

        // Assert
        Assert.Equal(1, runner.MaxConcurrency);
    }

    [Fact]
    public async Task UpscaleAsync_RunnerReturnsWrongSize_ThrowsInvalidOperation()
    {
        // Arrange
        using var upscaler = new TiledUpscaler(new WrongSizeRunner(), "broken", Tiling());

        // Act
        var exception = await Record.ExceptionAsync(() => upscaler.UpscaleAsync(FakeModelRunner.Pattern(10, 10), null, PauseToken.None, Ct));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task UpscaleAsync_FourChannelImage_ThrowsArgumentException()
    {
        // Arrange
        using var upscaler = new TiledUpscaler(new FakeModelRunner(2), "nearest", Tiling());

        // Act
        var exception = await Record.ExceptionAsync(() => upscaler.UpscaleAsync(new PlanarImage(4, 8, 8), null, PauseToken.None, Ct));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_MinTileSizeAboveTileSize_Throws()
    {
        // Act
        var exception = Record.Exception(() => new TiledUpscaler(new FakeModelRunner(2), "nearest", Tiling(tileSize: 32, minTileSize: 64)));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Dispose_Called_DisposesRunner()
    {
        // Arrange
        var runner = new FakeModelRunner(2);
        var upscaler = new TiledUpscaler(runner, "nearest", Tiling());

        // Act
        upscaler.Dispose();

        // Assert
        Assert.True(runner.Disposed);
    }

    [Fact]
    public void Properties_FromRunnerAndName_AreExposed()
    {
        // Act
        using var upscaler = new TiledUpscaler(new FakeModelRunner(3), "x3", Tiling(tileSize: 100));

        // Assert
        Assert.Equal(("x3", 3, 100), (upscaler.ModelName, upscaler.Scale, upscaler.CurrentTileSize));
    }

    private static OnnxRuntimeException CreateOnnxRuntimeException(string message)
    {
        // The constructor and its ErrorCode enum are internal to ONNX Runtime; build the exception the runtime would throw.
        var constructor = typeof(OnnxRuntimeException).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters() is [{ ParameterType.IsEnum: true }, { ParameterType: var text }] && text == typeof(string));
        var errorCode = Enum.Parse(constructor.GetParameters()[0].ParameterType, "RuntimeException");
        return (OnnxRuntimeException)constructor.Invoke([errorCode, message]);
    }

    private sealed class SynchronousProgress : IProgress<TileProgress>
    {
        public List<TileProgress> Reports { get; } = [];

        public void Report(TileProgress value) => Reports.Add(value);
    }

    private sealed class WrongSizeRunner : IModelRunner
    {
        public int Scale => 2;

        public PlanarImage Run(PlanarImage tile) => new(3, tile.Width, tile.Height);

        public void Dispose()
        {
        }
    }
}
