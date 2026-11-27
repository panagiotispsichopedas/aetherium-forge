using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

public sealed class OnnxModelRunnerTests : IDisposable
{
    private static readonly OnnxRunnerOptions _cpu = new() { UseDirectML = false };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Create_NoScaleMetadata_ProbesScale()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "x4", OnnxTestModels.NearestUpscale(4));

        // Act
        using var runner = OnnxModelRunner.Create(path, _cpu);

        // Assert
        Assert.Equal(4, runner.Scale);
    }

    [Fact]
    public void Create_ScaleMetadata_UsesMetadataWithoutProbing()
    {
        // Arrange: the metadata deliberately disagrees with the graph to prove it is read.
        var path = OnnxTestModels.WriteTemp(_folder, "x2", OnnxTestModels.NearestUpscale(2, scaleMetadata: "3"));

        // Act
        using var runner = OnnxModelRunner.Create(path, _cpu);

        // Assert
        Assert.Equal(3, runner.Scale);
    }

    [Fact]
    public void Create_InvalidScaleMetadata_FallsBackToProbe()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "x2", OnnxTestModels.NearestUpscale(2, scaleMetadata: "four"));

        // Act
        using var runner = OnnxModelRunner.Create(path, _cpu);

        // Assert
        Assert.Equal(2, runner.Scale);
    }

    [Fact]
    public void Create_CustomTensorNames_ReadsNamesFromSession()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "named", OnnxTestModels.NearestUpscale(2, inputName: "lr_image", outputName: "sr_image"));

        // Act
        using var runner = OnnxModelRunner.Create(path, _cpu);

        // Assert
        Assert.Equal(("lr_image", "sr_image", 2), (runner.InputName, runner.OutputName, runner.Scale));
    }

    [Fact]
    public void Create_MissingFile_ThrowsFileNotFound()
    {
        // Act
        var exception = Record.Exception(() => OnnxModelRunner.Create(Path.Combine(_folder, "missing.onnx"), _cpu));

        // Assert
        Assert.IsType<FileNotFoundException>(exception);
    }

    [Fact]
    public void Run_Fp32Model_ReturnsNearestUpscale()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "x4", OnnxTestModels.NearestUpscale(4));
        using var runner = OnnxModelRunner.Create(path, _cpu);
        var tile = FakeModelRunner.Pattern(7, 5);

        // Act
        var output = runner.Run(tile);

        // Assert
        Assert.Equal(FakeModelRunner.Apply(tile, 4, value => value).Data, output.Data);
    }

    [Fact]
    public void Run_Fp16Model_ConvertsAtBoundary()
    {
        // Arrange: Pattern values are k / 255; compare within FP16 precision.
        var path = OnnxTestModels.WriteTemp(_folder, "x2-fp16", OnnxTestModels.NearestUpscale(2, float16: true));
        using var runner = OnnxModelRunner.Create(path, _cpu);
        var tile = FakeModelRunner.Pattern(6, 9);
        var expected = FakeModelRunner.Apply(tile, 2, value => value);

        // Act
        var output = runner.Run(tile);

        // Assert
        Assert.Equal((true, 2, 12, 18), (runner.IsFloat16, runner.Scale, output.Width, output.Height));
        Assert.All(expected.Data.Zip(output.Data), pair => Assert.Equal(pair.First, pair.Second, 1e-3f));
    }

    [Fact]
    public void Run_WrongChannelCount_ThrowsArgumentException()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "x2", OnnxTestModels.NearestUpscale(2));
        using var runner = OnnxModelRunner.Create(path, _cpu);

        // Act
        var exception = Record.Exception(() => runner.Run(new PlanarImage(1, 4, 4)));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task UpscaleAsync_OnnxRunnerOnCpu_MatchesWholeImage()
    {
        // Arrange
        var path = OnnxTestModels.WriteTemp(_folder, "x2", OnnxTestModels.NearestUpscale(2));
        using var upscaler = new TiledUpscaler(OnnxModelRunner.Create(path, _cpu), "x2", new TilingOptions { TileSize = 32, Overlap = 4, MinTileSize = 16 });
        var source = FakeModelRunner.Pattern(77, 50);

        // Act
        var output = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(FakeModelRunner.Apply(source, 2, value => value).Data, output.Data);
    }

    [Fact]
    public async Task Create_DirectMLOnLocalAdapter_RunsModel()
    {
        // Needs a DirectX 12 GPU; opt in with AETHERIUM_TEST_DIRECTML=1 (optionally AETHERIUM_TEST_DIRECTML_DEVICE=<adapter index>).
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML") == "1", "Set AETHERIUM_TEST_DIRECTML=1 to test the DirectML provider.");

        // Arrange
        var deviceId = int.TryParse(Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML_DEVICE"), out var device) ? device : 0;
        var path = OnnxTestModels.WriteTemp(_folder, "x4", OnnxTestModels.NearestUpscale(4));
        using var runner = OnnxModelRunner.Create(path, new OnnxRunnerOptions { DeviceId = deviceId, UseDirectML = true });
        using var upscaler = new TiledUpscaler(runner, "x4", new TilingOptions { TileSize = 64, Overlap = 8, MinTileSize = 32 });
        var source = FakeModelRunner.Pattern(150, 90);

        // Act
        var output = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal((true, 4), (runner.UsesDirectML, runner.Scale));
        Assert.Equal(FakeModelRunner.Apply(source, 4, value => value).Data, output.Data);
    }

    [Fact]
    public async Task UpscaleAsync_RealModelFromEnvironment_ProducesScaledOutput()
    {
        // Opt in with AETHERIUM_TEST_MODEL=<path to .onnx>; AETHERIUM_TEST_DIRECTML=1 runs it on the GPU.
        var modelPath = Environment.GetEnvironmentVariable("AETHERIUM_TEST_MODEL");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath), "Set AETHERIUM_TEST_MODEL to a real .onnx model to run this test.");

        // Arrange
        var options = new OnnxRunnerOptions { UseDirectML = Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML") == "1" };
        using var upscaler = new TiledUpscaler(OnnxModelRunner.Create(modelPath!, options), "real", new TilingOptions { TileSize = 64, Overlap = 8, MinTileSize = 32 });
        var source = FakeModelRunner.Pattern(100, 80);

        // Act
        var output = await upscaler.UpscaleAsync(source, null, PauseToken.None, Ct);

        // Assert
        Assert.Equal((100 * upscaler.Scale, 80 * upscaler.Scale), (output.Width, output.Height));
    }
}
