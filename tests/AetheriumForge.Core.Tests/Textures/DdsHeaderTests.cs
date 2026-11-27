using System.Buffers.Binary;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Textures;

public sealed class DdsHeaderTests
{
    private static DdsHeader Parse(byte[] data)
    {
        Assert.True(DdsHeader.TryParse(data, out DdsHeader header));
        return header;
    }

    [Fact]
    public void TryParse_Dxt1Header_ReturnsDimensionsAndFormat()
    {
        // Arrange
        byte[] data = DdsTestData.Build(1024, 512, "DXT1", mipCount: 11);

        // Act
        bool ok = DdsHeader.TryParse(data, out DdsHeader header);

        // Assert
        Assert.True(ok);
        Assert.Equal(new DdsHeader(1024, 512, 11, "DXT1", null, false), header);
    }

    [Theory]
    [InlineData("DXT1")]
    [InlineData("DXT5")]
    [InlineData("ATI2")]
    [InlineData("BC5U")]
    public void FormatName_LegacyFourCC_ReturnsFourCC(string fourCC)
    {
        // Arrange
        DdsHeader header = Parse(DdsTestData.Build(256, 256, fourCC));

        // Act
        string name = header.FormatName;

        // Assert
        Assert.Equal(fourCC, name);
    }

    [Fact]
    public void TryParse_Dx10Bc7Header_ReadsDxgiFormat()
    {
        // Arrange
        byte[] data = DdsTestData.Build(2048, 2048, dxgiFormat: 98);

        // Act
        bool ok = DdsHeader.TryParse(data, out DdsHeader header);

        // Assert
        Assert.True(ok);
        Assert.Equal(new DdsHeader(2048, 2048, 1, "DX10", 98, false), header);
    }

    [Theory]
    [InlineData(98, "BC7_UNORM")]
    [InlineData(99, "BC7_UNORM")]
    [InlineData(71, "BC1_UNORM")]
    [InlineData(77, "BC3_UNORM")]
    [InlineData(83, "BC5_UNORM")]
    [InlineData(87, "B8G8R8A8_UNORM")]
    [InlineData(12345, "DXGI_12345")]
    public void FormatName_Dx10Header_MapsDxgiFormat(int dxgi, string expected)
    {
        // Arrange
        DdsHeader header = Parse(DdsTestData.Build(64, 64, dxgiFormat: dxgi));

        // Act
        string name = header.FormatName;

        // Assert
        Assert.Equal(expected, name);
    }

    [Fact]
    public void FormatName_NoFourCC_ReturnsUncompressed()
    {
        // Arrange
        DdsHeader header = Parse(DdsTestData.Build(64, 64, fourCC: null));

        // Act
        string name = header.FormatName;

        // Assert
        Assert.Equal("Uncompressed", name);
    }

    [Fact]
    public void FormatName_NumericD3DFormat_ReturnsD3DFmtName()
    {
        // Arrange
        byte[] data = DdsTestData.Build(64, 64, "DXT1");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(84), 113);
        DdsHeader header = Parse(data);

        // Act
        string name = header.FormatName;

        // Assert
        Assert.Equal("D3DFMT_113", name);
    }

    [Fact]
    public void TryParse_Caps2Cubemap_SetsIsCubemap()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256, "DXT5", cubemap: true);

        // Act
        DdsHeader header = Parse(data);

        // Assert
        Assert.True(header.IsCubemap);
    }

    [Fact]
    public void TryParse_Dx10MiscTextureCube_SetsIsCubemap()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256, dxgiFormat: 98, dx10Cube: true);

        // Act
        DdsHeader header = Parse(data);

        // Assert
        Assert.True(header.IsCubemap);
    }

    [Fact]
    public void TryParse_BadMagic_ReturnsFalse()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256);
        data[0] = (byte)'X';

        // Act
        bool ok = DdsHeader.TryParse(data, out DdsHeader header);

        // Assert
        Assert.False(ok);
        Assert.Equal(default, header);
    }

    [Fact]
    public void TryParse_BufferShorterThanLegacyHeader_ReturnsFalse()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256)[..127];

        // Act
        bool ok = DdsHeader.TryParse(data, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_Dx10HeaderTruncatedBeforeExtension_ReturnsFalse()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256, dxgiFormat: 98)[..(DdsHeader.MinimumLength - 1)];

        // Act
        bool ok = DdsHeader.TryParse(data, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsFalse()
    {
        // Act
        bool ok = DdsHeader.TryParse([], out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_ZeroWidth_ReturnsFalse()
    {
        // Arrange
        byte[] data = DdsTestData.Build(0, 256);

        // Act
        bool ok = DdsHeader.TryParse(data, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_ZeroMipCount_ReportsOneMip()
    {
        // Arrange
        byte[] data = DdsTestData.Build(256, 256, mipCount: 0);

        // Act
        DdsHeader header = Parse(data);

        // Assert
        Assert.Equal(1, header.MipCount);
    }

    [Fact]
    public void MaxDimension_NonSquare_ReturnsLargerSide()
    {
        // Arrange
        var header = new DdsHeader(256, 1024, 1, "DXT1", null, false);

        // Act
        int max = header.MaxDimension;

        // Assert
        Assert.Equal(1024, max);
    }
}
