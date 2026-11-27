using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Tests.Textures;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Imaging;

public sealed class RawDdsTests
{
    [Fact]
    public void Write_ThenRead_RoundTripsPixelsExactly()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Noise(37, 19);

        // Act
        RgbaImage read = RawDds.Read(RawDds.Write(image));

        // Assert
        Assert.Equal((image.Width, image.Height), (read.Width, read.Height));
        Assert.Equal(image.Pixels, read.Pixels);
    }

    [Fact]
    public void Write_Default_ProducesDx10R8G8B8A8UnormHeaderWithOneMip()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Noise(8, 4);

        // Act
        Assert.True(DdsHeader.TryParse(RawDds.Write(image), out DdsHeader header));

        // Assert
        Assert.Equal(new DdsHeader(8, 4, 1, "DX10", RawDds.DxgiR8G8B8A8Unorm, false), header);
    }

    [Fact]
    public void Write_Srgb_TagsDxgi29AndKeepsBytes()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Noise(5, 5);

        // Act
        byte[] dds = RawDds.Write(image, srgb: true);
        bool parsed = DdsHeader.TryParse(dds, out DdsHeader header);
        RgbaImage read = RawDds.Read(dds);

        // Assert
        Assert.Equal((true, RawDds.DxgiR8G8B8A8UnormSrgb), (parsed, header.DxgiFormat));
        Assert.Equal(image.Pixels, read.Pixels);
    }

    [Fact]
    public void Read_LegacyBgraMasks_SwizzlesToRgba()
    {
        // Arrange: texel bytes in memory are B, G, R, A.
        byte[] dds = ImagingTestData.LegacyDds(2, 1, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, [0x80112233u, 0xFF445566u]);

        // Act
        RgbaImage image = RawDds.Read(dds);

        // Assert
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x80, 0x44, 0x55, 0x66, 0xFF }, image.Pixels);
    }

    [Fact]
    public void Read_LegacyRgbaMasks_KeepsOrder()
    {
        // Arrange: texel bytes in memory are R, G, B, A.
        byte[] dds = ImagingTestData.LegacyDds(1, 1, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000, [0x40302010u]);

        // Act
        RgbaImage image = RawDds.Read(dds);

        // Assert
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, image.Pixels);
    }

    [Fact]
    public void Read_LegacyBgrxWithoutAlpha_SetsOpaqueAlpha()
    {
        // Arrange
        byte[] dds = ImagingTestData.LegacyDds(1, 1, 0x00FF0000, 0x0000FF00, 0x000000FF, 0, [0x00112233u]);

        // Act
        RgbaImage image = RawDds.Read(dds);

        // Assert
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, image.Pixels);
    }

    [Fact]
    public void Read_NotDds_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] data = new byte[200];

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => RawDds.Read(data));
    }

    [Fact]
    public void Read_CompressedFourCC_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] data = DdsTestData.Build(16, 16, "DXT5");
        Array.Resize(ref data, data.Length + 256);

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => RawDds.Read(data));
    }

    [Fact]
    public void Read_Dx10Bc7_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] data = DdsTestData.Build(16, 16, "DX10", dxgiFormat: 98);
        Array.Resize(ref data, data.Length + 1024);

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => RawDds.Read(data));
    }

    [Fact]
    public void Read_TruncatedPixels_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] dds = RawDds.Write(ImagingTestData.Noise(16, 16));

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => RawDds.Read(dds.AsSpan(0, dds.Length - 1)));
    }

    [Fact]
    public void Read_Legacy16BitFormat_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] dds = ImagingTestData.LegacyDds(1, 1, 0x00FF0000, 0x0000FF00, 0x000000FF, 0, [0u]);
        dds[88] = 16;

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => RawDds.Read(dds));
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsAndCreatesDirectories()
    {
        // Arrange
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "a", "b", "image.dds");
        RgbaImage image = ImagingTestData.Noise(9, 7);

        // Act
        await RawDds.WriteAsync(image, path, TestContext.Current.CancellationToken);
        RgbaImage read = await RawDds.ReadAsync(path, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(image.Pixels, read.Pixels);
    }
}
