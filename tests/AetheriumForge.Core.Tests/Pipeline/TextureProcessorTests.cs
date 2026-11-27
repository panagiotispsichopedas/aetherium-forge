using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Pipeline;

public sealed class TextureProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"af-proc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_Diffuse_UpscalesAndCommitsAtTargetSize()
    {
        // Arrange
        var (processor, codec, upscaler) = Create(Solid(16, 8, 200, 100, 50, 255));

        // Act
        var result = await processor.ProcessAsync(Job(TextureKind.Diffuse, 48, 24), null, PauseToken.None, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((48, 24, true, 1, false), (result.Width, result.Height, result.UsedModel, upscaler.Calls, result.HadAlpha));
    }

    [Fact]
    public async Task ProcessAsync_Diffuse_EncodedPixelsKeepColour()
    {
        var (processor, codec, _) = Create(Solid(16, 8, 200, 100, 50, 255));

        await processor.ProcessAsync(Job(TextureKind.Diffuse, 48, 24), null, PauseToken.None, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 200, 100, 50, 255 }, codec.Encoded!.Pixels[..4]);
    }

    [Fact]
    public async Task ProcessAsync_Parallax_SkipsModel()
    {
        var (processor, _, upscaler) = Create(Solid(16, 16, 128, 128, 128, 255));

        var result = await processor.ProcessAsync(Job(TextureKind.Parallax, 64, 64), null, PauseToken.None, TestContext.Current.CancellationToken);

        Assert.Equal((false, 0), (result.UsedModel, upscaler.Calls));
    }

    [Fact]
    public async Task ProcessAsync_TranslucentAlpha_KeepsAlphaOutOfModel()
    {
        // Arrange
        var (processor, codec, upscaler) = Create(Solid(8, 8, 10, 20, 30, 77));

        // Act
        var result = await processor.ProcessAsync(Job(TextureKind.Diffuse, 32, 32), null, PauseToken.None, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((true, 3, (byte)77), (result.HadAlpha, upscaler.LastInputChannels, codec.Encoded!.Pixels[3]));
    }

    [Fact]
    public async Task ProcessAsync_NormalMap_OutputVectorsAreUnitLength()
    {
        // Arrange: a tilted normal (x=0.6, z=0.8) encoded as RGB.
        var (processor, codec, _) = Create(Solid(8, 8, 204, 128, 230, 255));

        // Act
        await processor.ProcessAsync(Job(TextureKind.Normal, 32, 32, normalStrength: 1.4), null, PauseToken.None, TestContext.Current.CancellationToken);

        // Assert
        var p = codec.Encoded!.Pixels;
        double x = (p[0] / 127.5) - 1, y = (p[1] / 127.5) - 1, z = (p[2] / 127.5) - 1;
        Assert.InRange(Math.Sqrt((x * x) + (y * y) + (z * z)), 0.98, 1.02);
    }

    [Fact]
    public async Task ProcessAsync_SrgbSource_EncodesSrgb()
    {
        var (processor, codec, _) = Create(Solid(8, 8, 1, 2, 3, 255));

        await processor.ProcessAsync(Job(TextureKind.Diffuse, 32, 32) with { SourceDxgiFormat = 99 }, null, PauseToken.None, TestContext.Current.CancellationToken);

        Assert.True(codec.EncodedSrgb);
    }

    [Fact]
    public async Task ProcessAsync_Cancelled_CommitsNothingAndCleansWork()
    {
        // Arrange
        var (processor, codec, _) = Create(Solid(8, 8, 1, 2, 3, 255));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(Job(TextureKind.Diffuse, 32, 32), null, PauseToken.None, cts.Token));

        // Assert
        Assert.Equal((null, 0), (codec.Encoded, Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")).Count()));
    }

    [Fact]
    public void GetOutputPath_CanonicalPath_UnderOutputRoot()
    {
        var (processor, _, _) = Create(Solid(1, 1, 0, 0, 0, 255));

        var path = processor.GetOutputPath(@"textures\a\b.dds");

        Assert.Equal(Path.Combine(_root, "out", "textures", "a", "b.dds"), path);
    }

    private static TextureJob Job(TextureKind kind, int width, int height, double normalStrength = 1.0) =>
        new(@"textures\test\a.dds", "Skyrim - Textures0.bsa", kind, width, height, "fake", normalStrength);

    private static RgbaImage Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var image = new RgbaImage(width, height);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = r;
            image.Pixels[i + 1] = g;
            image.Pixels[i + 2] = b;
            image.Pixels[i + 3] = a;
        }

        return image;
    }

    private (TextureProcessor Processor, FakeCodec Codec, FakeUpscaler Upscaler) Create(RgbaImage decoded)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var codec = new FakeCodec(decoded);
        var upscaler = new FakeUpscaler();
        var processor = new TextureProcessor(new FakeSource(), codec, _ => upscaler, Path.Combine(_root, "out"), work);
        return (processor, codec, upscaler);
    }

    private sealed class FakeSource : ITextureSource
    {
        public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 1, 2, 3 });
    }

    private sealed class FakeCodec(RgbaImage decoded) : ITextureCodec
    {
        public RgbaImage? Encoded { get; private set; }

        public bool EncodedSrgb { get; private set; }

        public Task<RgbaImage> DecodeAsync(string ddsPath, string workFolder, CancellationToken ct = default) => Task.FromResult(decoded);

        public Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, bool srgb, CancellationToken ct = default)
        {
            Encoded = image;
            EncodedSrgb = srgb;
            return Task.CompletedTask;
        }
    }

    /// <summary>Nearest-neighbour ×4 stand-in for the AI model.</summary>
    private sealed class FakeUpscaler : IUpscaler
    {
        public int Calls { get; private set; }

        public int LastInputChannels { get; private set; }

        public string ModelName => "fake";

        public int Scale => 4;

        public Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
        {
            Calls++;
            LastInputChannels = rgb.Channels;
            var output = new PlanarImage(rgb.Channels, rgb.Width * Scale, rgb.Height * Scale);
            for (var c = 0; c < rgb.Channels; c++)
            {
                for (var y = 0; y < output.Height; y++)
                {
                    for (var x = 0; x < output.Width; x++)
                    {
                        output.Data[output.IndexOf(c, x, y)] = rgb.Data[rgb.IndexOf(c, x / Scale, y / Scale)];
                    }
                }
            }

            return Task.FromResult(output);
        }

        public void Dispose()
        {
        }
    }
}
