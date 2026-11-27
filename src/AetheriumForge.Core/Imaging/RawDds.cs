using System.Buffers.Binary;
using System.Numerics;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Reads and writes uncompressed 32-bit RGBA DDS files, the interchange format between Aetherium Forge and texconv
/// (IMPLEMENTATION_GUIDE §10, with uncompressed DDS replacing PNG).
/// </summary>
/// <remarks>
/// Pixel values are copied byte for byte: no gamma conversion happens here. The sRGB DXGI variant only changes the
/// format tag in the header, which tells texconv how to interpret the same bytes.
/// </remarks>
public static class RawDds
{
    /// <summary>DXGI_FORMAT_R8G8B8A8_UNORM.</summary>
    public const int DxgiR8G8B8A8Unorm = 28;

    /// <summary>DXGI_FORMAT_R8G8B8A8_UNORM_SRGB.</summary>
    public const int DxgiR8G8B8A8UnormSrgb = 29;

    private const int MagicLength = 4;
    private const int HeaderLength = 124;
    private const int Dx10HeaderLength = 20;
    private const int LegacyDataOffset = MagicLength + HeaderLength;
    private const int Dx10DataOffset = LegacyDataOffset + Dx10HeaderLength;

    private const uint Magic = 0x20534444; // "DDS "
    private const uint Dx10FourCC = 0x30315844; // "DX10"

    private const uint FlagCaps = 0x1;
    private const uint FlagHeight = 0x2;
    private const uint FlagWidth = 0x4;
    private const uint FlagPitch = 0x8;
    private const uint FlagPixelFormat = 0x1000;
    private const uint FlagMipMapCount = 0x20000;

    private const uint PixelFormatAlphaPixels = 0x1;
    private const uint PixelFormatFourCC = 0x4;
    private const uint PixelFormatRgb = 0x40;

    private const uint CapsTexture = 0x1000;
    private const uint ResourceDimensionTexture2D = 3;

    /// <summary>
    /// Reads the top mip level of an uncompressed 32-bit DDS image.
    /// </summary>
    /// <param name="dds">The whole DDS file.</param>
    /// <returns>The top mip level as RGBA8. For arrays and cube maps this is the first image.</returns>
    /// <exception cref="InvalidDataException">
    /// The data is not a DDS file, is truncated, or has a pixel format other than DX10 R8G8B8A8_UNORM(_SRGB) or a legacy
    /// 32-bit RGB(A) mask layout with byte-aligned masks.
    /// </exception>
    public static RgbaImage Read(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < LegacyDataOffset || BinaryPrimitives.ReadUInt32LittleEndian(dds) != Magic)
        {
            throw new InvalidDataException("Not a DDS file (missing 'DDS ' magic or header too short).");
        }

        int height = BinaryPrimitives.ReadInt32LittleEndian(dds[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dds[16..]);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid DDS dimensions {width}×{height}.");
        }

        uint pixelFormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(dds[80..]);
        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(dds[84..]);

        if ((pixelFormatFlags & PixelFormatFourCC) != 0)
        {
            if (fourCC != Dx10FourCC)
            {
                throw new InvalidDataException("Compressed or FourCC DDS formats are not supported; expected uncompressed RGBA.");
            }

            if (dds.Length < Dx10DataOffset)
            {
                throw new InvalidDataException("DDS DX10 extension header is truncated.");
            }

            int dxgi = BinaryPrimitives.ReadInt32LittleEndian(dds[128..]);
            if (dxgi is not (DxgiR8G8B8A8Unorm or DxgiR8G8B8A8UnormSrgb))
            {
                throw new InvalidDataException($"Unsupported DXGI format {dxgi}; expected R8G8B8A8_UNORM (28) or R8G8B8A8_UNORM_SRGB (29).");
            }

            ReadOnlySpan<byte> pixels = SlicePixels(dds, Dx10DataOffset, width, height);
            return new RgbaImage(width, height, pixels.ToArray());
        }

        if ((pixelFormatFlags & PixelFormatRgb) == 0)
        {
            throw new InvalidDataException("Unsupported legacy DDS pixel format; expected 32-bit RGB(A).");
        }

        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(dds[88..]);
        if (bitCount != 32)
        {
            throw new InvalidDataException($"Unsupported legacy DDS bit count {bitCount}; expected 32.");
        }

        uint redMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[92..]);
        uint greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[96..]);
        uint blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dds[100..]);
        uint alphaMask = (pixelFormatFlags & PixelFormatAlphaPixels) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(dds[104..]) : 0;

        int redShift = GetByteShift(redMask, "red");
        int greenShift = GetByteShift(greenMask, "green");
        int blueShift = GetByteShift(blueMask, "blue");
        int alphaShift = alphaMask == 0 ? -1 : GetByteShift(alphaMask, "alpha");

        ReadOnlySpan<byte> source = SlicePixels(dds, LegacyDataOffset, width, height);
        var image = new RgbaImage(width, height);
        Span<byte> target = image.Pixels;
        for (int i = 0; i < target.Length; i += 4)
        {
            uint texel = BinaryPrimitives.ReadUInt32LittleEndian(source[i..]);
            target[i] = (byte)(texel >> redShift);
            target[i + 1] = (byte)(texel >> greenShift);
            target[i + 2] = (byte)(texel >> blueShift);
            target[i + 3] = alphaShift < 0 ? (byte)255 : (byte)(texel >> alphaShift);
        }

        return image;
    }

    /// <summary>
    /// Reads the top mip level of an uncompressed 32-bit DDS file.
    /// </summary>
    /// <param name="path">The DDS file path.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The top mip level as RGBA8.</returns>
    /// <exception cref="InvalidDataException">The file is not a supported uncompressed DDS image.</exception>
    public static async Task<RgbaImage> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Read(data);
    }

    /// <summary>
    /// Writes an image as a DX10 DDS with format R8G8B8A8_UNORM and a single mip level.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns>The complete DDS file.</returns>
    public static byte[] Write(RgbaImage image) => Write(image, srgb: false);

    /// <summary>
    /// Writes an image as a DX10 DDS with a single mip level, tagged R8G8B8A8_UNORM or R8G8B8A8_UNORM_SRGB.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="srgb">
    /// <see langword="true"/> to tag the pixels as sRGB (DXGI 29). The bytes are written unchanged either way; the tag
    /// must match the encoder's target format so that texconv does not apply a gamma conversion.
    /// </param>
    /// <returns>The complete DDS file.</returns>
    public static byte[] Write(RgbaImage image, bool srgb)
    {
        ArgumentNullException.ThrowIfNull(image);

        int pitch = checked(image.Width * 4);
        byte[] data = new byte[Dx10DataOffset + image.Pixels.Length];
        Span<byte> span = data;

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], FlagCaps | FlagHeight | FlagWidth | FlagPitch | FlagPixelFormat | FlagMipMapCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], image.Height);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], pitch);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], 1); // mip count

        // Pixel format (offset 76).
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], PixelFormatFourCC);
        BinaryPrimitives.WriteUInt32LittleEndian(span[84..], Dx10FourCC);

        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], CapsTexture);

        // DX10 extension header (offset 128).
        BinaryPrimitives.WriteInt32LittleEndian(span[128..], srgb ? DxgiR8G8B8A8UnormSrgb : DxgiR8G8B8A8Unorm);
        BinaryPrimitives.WriteUInt32LittleEndian(span[132..], ResourceDimensionTexture2D);
        BinaryPrimitives.WriteUInt32LittleEndian(span[136..], 0); // misc flags
        BinaryPrimitives.WriteUInt32LittleEndian(span[140..], 1); // array size
        BinaryPrimitives.WriteUInt32LittleEndian(span[144..], 0); // misc flags 2 (alpha mode unknown)

        image.Pixels.CopyTo(span[Dx10DataOffset..]);
        return data;
    }

    /// <summary>
    /// Writes an image to a file as a DX10 DDS with format R8G8B8A8_UNORM and a single mip level.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="path">The target path; parent directories are created.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static Task WriteAsync(RgbaImage image, string path, CancellationToken ct = default) => WriteAsync(image, path, srgb: false, ct);

    /// <summary>
    /// Writes an image to a file as a DX10 DDS with a single mip level, tagged R8G8B8A8_UNORM or R8G8B8A8_UNORM_SRGB.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="path">The target path; parent directories are created.</param>
    /// <param name="srgb"><see langword="true"/> to tag the pixels as sRGB; see <see cref="Write(RgbaImage, bool)"/>.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async Task WriteAsync(RgbaImage image, string path, bool srgb, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, Write(image, srgb), ct).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> SlicePixels(ReadOnlySpan<byte> dds, int offset, int width, int height)
    {
        long length = (long)width * height * 4;
        if (length > int.MaxValue || dds.Length - offset < length)
        {
            throw new InvalidDataException($"DDS pixel data is truncated: expected {length} bytes for {width}×{height}, found {Math.Max(0, dds.Length - offset)}.");
        }

        return dds.Slice(offset, (int)length);
    }

    private static int GetByteShift(uint mask, string channel)
    {
        int shift = BitOperations.TrailingZeroCount(mask);
        if (mask == 0 || shift % 8 != 0 || mask >> shift != 0xFF)
        {
            throw new InvalidDataException($"Unsupported legacy DDS {channel} mask 0x{mask:X8}; expected a byte-aligned 8-bit mask.");
        }

        return shift;
    }
}
