using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AetheriumForge.Core.Textures;

/// <summary>
/// The fields of a DDS file header that Aetherium Forge needs to classify and size a texture (IMPLEMENTATION_GUIDE §8).
/// </summary>
/// <param name="Width">Width of the top mip level in pixels.</param>
/// <param name="Height">Height of the top mip level in pixels.</param>
/// <param name="MipCount">Number of mip levels (at least 1 for a parsed header).</param>
/// <param name="FourCC">The pixel-format FourCC (for example <c>DXT1</c> or <c>DX10</c>); empty for uncompressed formats.</param>
/// <param name="DxgiFormat">The DXGI format from the DX10 extension header, or <see langword="null"/> when there is no extension header.</param>
/// <param name="IsCubemap"><see langword="true"/> when the texture is a cube map.</param>
public readonly record struct DdsHeader(int Width, int Height, int MipCount, string FourCC, int? DxgiFormat, bool IsCubemap)
{
    /// <summary>
    /// Number of bytes needed to parse any header, including the DX10 extension (4-byte magic + 124-byte header + 20-byte DX10 header).
    /// Headers without the DX10 extension need only <see cref="LegacyLength"/> bytes.
    /// </summary>
    public const int MinimumLength = 148;

    /// <summary>
    /// Number of bytes needed to parse a header without the DX10 extension (4-byte magic + 124-byte header).
    /// </summary>
    public const int LegacyLength = 128;

    private const uint Magic = 0x20534444; // "DDS " little-endian
    private const uint PixelFormatFourCCFlag = 0x4;
    private const uint Caps2Cubemap = 0x200;
    private const uint Dx10MiscTextureCube = 0x4;
    private const uint Dx10FourCC = 0x30315844; // "DX10" little-endian

    /// <summary>
    /// Gets a short, human-readable name of the pixel format, for example <c>DXT1</c>, <c>DXT5</c>, <c>ATI2</c> or <c>BC7_UNORM</c>.
    /// </summary>
    /// <remarks>
    /// DX10 headers map known DXGI formats to their name (sRGB variants collapse to the base name) and fall back to <c>DXGI_&lt;n&gt;</c>.
    /// Legacy headers return the FourCC, or <c>Uncompressed</c> when there is none.
    /// </remarks>
    public string FormatName => DxgiFormat is int dxgi
        ? GetDxgiName(dxgi)
        : string.IsNullOrEmpty(FourCC) ? "Uncompressed" : FourCC;

    /// <summary>
    /// Gets the larger of <see cref="Width"/> and <see cref="Height"/> (the "source max" used by the rules).
    /// </summary>
    public int MaxDimension => Math.Max(Width, Height);

    /// <summary>
    /// Parses a DDS header from the start of a file.
    /// </summary>
    /// <param name="data">The first bytes of the file: at least <see cref="LegacyLength"/> bytes, or <see cref="MinimumLength"/> when the header has a DX10 extension.</param>
    /// <param name="header">The parsed header, or <see langword="default"/> when parsing fails.</param>
    /// <returns><see langword="true"/> when the magic is <c>DDS </c>, the buffer is long enough and the dimensions are positive.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out DdsHeader header)
    {
        header = default;
        if (data.Length < LegacyLength || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            return false;
        }

        int height = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        int mipCount = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(data[28..]));
        uint pixelFormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        uint fourCCValue = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        uint caps2 = BinaryPrimitives.ReadUInt32LittleEndian(data[112..]);

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bool hasFourCC = (pixelFormatFlags & PixelFormatFourCCFlag) != 0 && fourCCValue != 0;
        string fourCC = hasFourCC ? DecodeFourCC(data.Slice(84, 4), fourCCValue) : string.Empty;
        bool isCubemap = (caps2 & Caps2Cubemap) != 0;
        int? dxgiFormat = null;

        if (hasFourCC && fourCCValue == Dx10FourCC)
        {
            if (data.Length < MinimumLength)
            {
                return false;
            }

            dxgiFormat = BinaryPrimitives.ReadInt32LittleEndian(data[128..]);
            uint miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(data[136..]);
            isCubemap |= (miscFlag & Dx10MiscTextureCube) != 0;
        }

        header = new DdsHeader(width, height, mipCount, fourCC, dxgiFormat, isCubemap);
        return true;
    }

    private static string DecodeFourCC(ReadOnlySpan<byte> bytes, uint value)
    {
        foreach (byte b in bytes)
        {
            if (b is < 0x20 or > 0x7E)
            {
                // Legacy D3DFMT values (for example 113 = A16B16G16R16F) are stored as numbers, not characters.
                return string.Create(CultureInfo.InvariantCulture, $"D3DFMT_{value}");
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static string GetDxgiName(int dxgi) => dxgi switch
    {
        2 => "R32G32B32A32_FLOAT",
        10 => "R16G16B16A16_FLOAT",
        28 or 29 => "R8G8B8A8_UNORM",
        61 => "R8_UNORM",
        65 => "A8_UNORM",
        70 or 71 or 72 => "BC1_UNORM",
        73 or 74 or 75 => "BC2_UNORM",
        76 or 77 or 78 => "BC3_UNORM",
        79 or 80 => "BC4_UNORM",
        81 => "BC4_SNORM",
        82 or 83 => "BC5_UNORM",
        84 => "BC5_SNORM",
        85 => "B5G6R5_UNORM",
        87 or 91 => "B8G8R8A8_UNORM",
        88 or 93 => "B8G8R8X8_UNORM",
        94 or 95 => "BC6H_UF16",
        96 => "BC6H_SF16",
        97 or 98 or 99 => "BC7_UNORM",
        _ => string.Create(CultureInfo.InvariantCulture, $"DXGI_{dxgi}"),
    };
}
