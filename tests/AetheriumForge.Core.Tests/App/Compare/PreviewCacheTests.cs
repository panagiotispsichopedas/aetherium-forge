using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class PreviewCacheTests
{
    private static PreviewSample Sample(string name, int size = 4)
    {
        var request = new PreviewRequest($"textures\\{name}.dds", "a.bsa", TextureKind.Diffuse, size, size, "fake");
        var planes = new PlanarImage(3, size, size);
        for (var i = 0; i < planes.Data.Length; i++)
        {
            planes.Data[i] = i % 17 / 16f;
        }

        return new PreviewSample(request, new RgbaImage(2, 2), planes, new PlanarImage(3, size, size), new PlanarImage(1, size, size), usedModel: true);
    }

    [Fact]
    public void Add_OverCapacity_EvictsOldestFirst()
    {
        // Arrange
        var a = Sample("a");
        var cache = new PreviewMemoryCache((a.SizeInBytes * 2) + 1);
        cache.Add(a);
        cache.Add(Sample("b"));

        // Act
        var evicted = cache.Add(Sample("c"));

        // Assert
        Assert.Equal([a.Request.CacheKey], evicted);
    }

    [Fact]
    public void Add_SingleSampleLargerThanCap_KeepsIt()
    {
        // Arrange
        var cache = new PreviewMemoryCache(1);

        // Act
        cache.Add(Sample("big"));

        // Assert
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void CacheKey_PathCaseAndSlashes_AreNormalised()
    {
        // Arrange
        var upper = new PreviewRequest("Textures/Clutter/A.dds", "A.bsa", TextureKind.Diffuse, 4, 4, "Fake");
        var lower = new PreviewRequest("textures\\clutter\\a.dds", "a.bsa", TextureKind.Diffuse, 4, 4, "fake");

        // Act
        var same = upper.CacheKey == lower.CacheKey;

        // Assert
        Assert.True(same);
    }

    [Fact]
    public void DiskEncoding_RoundTrip_KeepsPlanesWithinHalfPrecision()
    {
        // Arrange
        var sample = Sample("a");

        // Act
        var decoded = PreviewDiskCache.Decode(sample.Request, PreviewDiskCache.Encode(sample))!;

        // Assert
        Assert.All(sample.AiRgb.Data.Zip(decoded.AiRgb.Data), pair => Assert.Equal(pair.First, pair.Second, 0.001f));
    }

    [Fact]
    public void DiskDecode_OtherRequest_ReturnsNull()
    {
        // Arrange
        var bytes = PreviewDiskCache.Encode(Sample("a"));

        // Act
        var decoded = PreviewDiskCache.Decode(Sample("b").Request, bytes);

        // Assert
        Assert.Null(decoded);
    }

    [Fact]
    public void ChannelConvert_Alpha_ShowsAlphaAsOpaqueGrey()
    {
        // Arrange
        var image = new RgbaImage(1, 1, [10, 20, 30, 77]);

        // Act
        var view = ChannelView.Convert(image, ChannelMode.Alpha);

        // Assert
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, view.Pixels);
    }

    [Fact]
    public void ChannelConvert_Normal_ShowsXyWithoutBlue()
    {
        // Arrange
        var image = new RgbaImage(1, 1, [200, 60, 255, 0]);

        // Act
        var view = ChannelView.Convert(image, ChannelMode.Normal);

        // Assert
        Assert.Equal(new byte[] { 200, 60, 0, 255 }, view.Pixels);
    }
}
