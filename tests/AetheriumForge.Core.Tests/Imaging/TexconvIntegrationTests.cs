using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Imaging;

/// <summary>
/// Runs the real texconv. Skipped unless the <c>AETHERIUM_TEXCONV</c> environment variable points to <c>texconv.exe</c>.
/// </summary>
public sealed class TexconvIntegrationTests
{
    private static Texconv Create(bool useGpu = true)
    {
        string? path = ImagingTestData.TexconvPath;
        Assert.SkipUnless(path is not null, $"Set {ImagingTestData.TexconvVariable} to texconv.exe to run texconv integration tests.");
        return new Texconv(new TexconvOptions { ExecutablePath = path!, UseGpu = useGpu });
    }

    [Fact]
    public async Task GetVersionAsync_RealTexconv_ReturnsDottedVersion()
    {
        // Arrange
        Texconv texconv = Create();

        // Act
        string version = await texconv.GetVersionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Matches(@"^\d+(\.\d+)+$", version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_UncompressedSource_IsPixelExact(bool srgb)
    {
        // Arrange
        Texconv texconv = Create();
        using var folder = new TempFolder();
        RgbaImage image = ImagingTestData.Noise(37, 23);
        string source = Path.Combine(folder.Path, "source.dds");
        await RawDds.WriteAsync(image, source, srgb, TestContext.Current.CancellationToken);

        // Act
        RgbaImage decoded = await texconv.DecodeAsync(source, Path.Combine(folder.Path, "work"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((image.Width, image.Height), (decoded.Width, decoded.Height));
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(false, true, 98)]
    [InlineData(true, true, 99)]
    [InlineData(false, false, 98)]
    public async Task EncodeBc7Async_ThenDecode_RoundTripsWithinBc7Tolerance(bool srgb, bool useGpu, int expectedDxgi)
    {
        // Arrange
        Texconv texconv = Create(useGpu);
        using var folder = new TempFolder();
        RgbaImage image = ImagingTestData.Gradient(128, 64);
        string output = Path.Combine(folder.Path, "final", "textures", "result.dds");
        string work = Path.Combine(folder.Path, "work");

        // Act
        await texconv.EncodeBc7Async(image, output, work, srgb, TestContext.Current.CancellationToken);
        byte[] header = (await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken))[..DdsHeader.MinimumLength];
        RgbaImage decoded = await texconv.DecodeAsync(output, work, TestContext.Current.CancellationToken);

        // Assert: BC7 format, full mip chain (128 → 1 is 8 levels), values close to the source, temp files removed.
        Assert.True(DdsHeader.TryParse(header, out DdsHeader parsed));
        Assert.Equal((128, 64, 8, expectedDxgi), (parsed.Width, parsed.Height, parsed.MipCount, parsed.DxgiFormat));
        (int max, double mean) = Difference(image, decoded);
        Assert.True(max <= 12 && mean <= 1.5, $"max abs diff {max}, mean {mean:F3}");
        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public async Task EncodeBc7Async_FolderNamedFailed_Succeeds()
    {
        // Arrange: texconv echoes every path, so the folder name shows up in its output.
        Texconv texconv = Create();
        using var folder = new TempFolder();
        string root = Path.Combine(folder.Path, "FAILED");
        string output = Path.Combine(root, "textures", "result.dds");
        string work = Path.Combine(root, "work");

        // Act
        await texconv.EncodeBc7Async(ImagingTestData.Gradient(16, 16), output, work, srgb: false, TestContext.Current.CancellationToken);
        RgbaImage decoded = await texconv.DecodeAsync(output, work, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((16, 16), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task EncodeBc7Async_ExistingOutput_IsReplaced()
    {
        // Arrange
        Texconv texconv = Create();
        using var folder = new TempFolder();
        string output = Path.Combine(folder.Path, "result.dds");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);

        // Act
        await texconv.EncodeBc7Async(ImagingTestData.Gradient(16, 16), output, Path.Combine(folder.Path, "work"), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(new FileInfo(output).Length > 100);
    }

    [Fact]
    public async Task DecodeAsync_MissingSource_ThrowsFileNotFound()
    {
        // Arrange
        Texconv texconv = Create();
        using var folder = new TempFolder();

        // Act + Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => texconv.DecodeAsync(Path.Combine(folder.Path, "missing.dds"), folder.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DecodeAsync_CorruptSource_ThrowsTexconvException()
    {
        // Arrange
        Texconv texconv = Create();
        using var folder = new TempFolder();
        string source = Path.Combine(folder.Path, "corrupt.dds");
        await File.WriteAllBytesAsync(source, new byte[64], TestContext.Current.CancellationToken);

        // Act
        TexconvException ex = await Assert.ThrowsAsync<TexconvException>(() => texconv.DecodeAsync(source, folder.Path, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("FAILED", ex.OutputTail, StringComparison.Ordinal);
    }

    private static (int Max, double Mean) Difference(RgbaImage expected, RgbaImage actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int max = 0;
        long total = 0;
        for (int i = 0; i < expected.Pixels.Length; i++)
        {
            int d = Math.Abs(expected.Pixels[i] - actual.Pixels[i]);
            max = Math.Max(max, d);
            total += d;
        }

        return (max, (double)total / expected.Pixels.Length);
    }
}
