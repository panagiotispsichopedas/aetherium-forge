using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

public sealed class UpscalerFactoryTests
{
    private static readonly TilingOptions _tiling = new() { TileSize = 64, Overlap = 8, MinTileSize = 16 };

    private static ModelInfo Model(string name, int? scale = null) => new(name, $@"C:\models\{name}.onnx", name, scale, "fp32", null, []);

    [Fact]
    public void Get_SameModelTwice_CreatesOneRunner()
    {
        // Arrange
        var created = 0;
        using var factory = new UpscalerFactory([Model("x4")], _ => { created++; return new FakeModelRunner(4); }, _tiling);

        // Act
        var first = factory.Get("x4");
        var second = factory.Get("X4");

        // Assert
        Assert.Equal((true, 1), (ReferenceEquals(first, second), created));
    }

    [Fact]
    public void Get_DifferentModels_ReturnsDistinctUpscalers()
    {
        // Arrange
        using var factory = new UpscalerFactory([Model("x2"), Model("x4")], model => new FakeModelRunner(model.Name == "x2" ? 2 : 4), _tiling);

        // Act
        var x2 = factory.Get("x2");
        var x4 = factory.Get("x4");

        // Assert
        Assert.Equal(("x2", 2, "x4", 4), (x2.ModelName, x2.Scale, x4.ModelName, x4.Scale));
    }

    [Fact]
    public void Get_UnknownModel_ThrowsKeyNotFound()
    {
        // Arrange
        using var factory = new UpscalerFactory([Model("x4")], _ => new FakeModelRunner(4), _tiling);

        // Act
        var exception = Record.Exception(() => factory.Get("missing"));

        // Assert
        Assert.IsType<KeyNotFoundException>(exception);
    }

    [Fact]
    public void Get_ConcurrentCalls_CreatesOneRunner()
    {
        // Arrange
        var created = 0;
        using var factory = new UpscalerFactory([Model("x4")], _ => { Interlocked.Increment(ref created); Thread.Sleep(20); return new FakeModelRunner(4); }, _tiling);

        // Act
        var upscalers = Enumerable.Range(0, 8).AsParallel().Select(_ => factory.Get("x4")).ToArray();

        // Assert
        Assert.Equal((1, 1), (created, upscalers.Distinct().Count()));
    }

    [Fact]
    public void Get_RunnerCreationFails_DoesNotCacheFailure()
    {
        // Arrange
        var attempts = 0;
        using var factory = new UpscalerFactory(
            [Model("x4")],
            _ => ++attempts == 1 ? throw new IOException("locked") : new FakeModelRunner(4),
            _tiling);
        var failure = Record.Exception(() => factory.Get("x4"));

        // Act
        var upscaler = factory.Get("x4");

        // Assert
        Assert.Equal((true, 4, 2), (failure is IOException, upscaler.Scale, attempts));
    }

    [Fact]
    public void Dispose_AfterGet_DisposesRunners()
    {
        // Arrange
        var runner = new FakeModelRunner(4);
        var factory = new UpscalerFactory([Model("x4")], _ => runner, _tiling);
        factory.Get("x4");

        // Act
        factory.Dispose();

        // Assert
        Assert.True(runner.Disposed);
    }

    [Fact]
    public void Get_AfterDispose_ThrowsObjectDisposed()
    {
        // Arrange
        var factory = new UpscalerFactory([Model("x4")], _ => new FakeModelRunner(4), _tiling);
        factory.Dispose();

        // Act
        var exception = Record.Exception(() => factory.Get("x4"));

        // Assert
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public void Constructor_DuplicateNames_ThrowsArgumentException()
    {
        // Act
        var exception = Record.Exception(() => new UpscalerFactory([Model("x4"), Model("X4")], _ => new FakeModelRunner(4), _tiling));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }
}
