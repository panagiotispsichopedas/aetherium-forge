using System.Buffers.Binary;
using System.Text;

namespace AetheriumForge.Core.Tests.Textures;

/// <summary>
/// Builds synthetic DDS headers for tests.
/// </summary>
internal static class DdsTestData
{
    public static byte[] Build(int width, int height, string? fourCC = "DXT1", int mipCount = 1, bool cubemap = false, int? dxgiFormat = null, bool dx10Cube = false)
    {
        bool dx10 = dxgiFormat is not null;
        byte[] data = new byte[dx10 ? 148 : 128];
        Span<byte> span = data;

        Encoding.ASCII.GetBytes("DDS ", span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 124);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], mipCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[76..], 32);

        string? effectiveFourCC = dx10 ? "DX10" : fourCC;
        if (!string.IsNullOrEmpty(effectiveFourCC))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x4);
            Encoding.ASCII.GetBytes(effectiveFourCC, span[84..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x41);
        }

        if (cubemap)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[112..], 0x200 | 0xFC00);
        }

        if (dx10)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[128..], dxgiFormat!.Value);
            BinaryPrimitives.WriteInt32LittleEndian(span[132..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(span[136..], dx10Cube ? 0x4u : 0u);
            BinaryPrimitives.WriteInt32LittleEndian(span[140..], 1);
        }

        return data;
    }
}
