using System.Text;

namespace AetheriumForge.Core.Archives;

/// <summary>
/// The TES4 / Skyrim SE name hash used by BSA archives to index folders and files (IMPLEMENTATION_GUIDE §6).
/// </summary>
/// <remarks>
/// Names are lowercased and forward slashes are converted to backslashes before hashing. Characters are hashed
/// as single bytes (Latin-1), which matches the 8-bit names stored in vanilla archives.
/// </remarks>
public static class TesHash
{
    private const uint Multiplier = 0x1003F;

    /// <summary>
    /// Computes the hash of a file name as stored in a BSA file record.
    /// </summary>
    /// <param name="fileName">The file name only, without its folder, for example <c>bucket01.dds</c>.</param>
    /// <returns>The 64-bit file hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is <see langword="null"/>.</exception>
    public static ulong HashFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        byte[] name = ToHashBytes(fileName);
        int dot = Array.LastIndexOf(name, (byte)'.');
        if (dot < 0)
        {
            return Compute(name, []);
        }

        return Compute(name.AsSpan(0, dot), name.AsSpan(dot));
    }

    /// <summary>
    /// Computes the hash of a folder path as stored in a BSA folder record.
    /// </summary>
    /// <param name="folderPath">The full folder path, for example <c>textures\clutter</c>. The whole path is hashed with no extension.</param>
    /// <returns>The 64-bit folder hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="folderPath"/> is <see langword="null"/>.</exception>
    public static ulong HashFolder(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        return Compute(ToHashBytes(folderPath), []);
    }

    private static byte[] ToHashBytes(string value) => Encoding.Latin1.GetBytes(value.Replace('/', '\\').ToLowerInvariant());

    private static ulong Compute(ReadOnlySpan<byte> root, ReadOnlySpan<byte> extension)
    {
        int length = root.Length;
        uint h1 = 0;
        if (length > 0)
        {
            h1 = root[^1]
                | (length > 2 ? (uint)root[^2] << 8 : 0u)
                | ((uint)length << 16)
                | ((uint)root[0] << 24);
        }

        h1 |= ExtensionBits(extension);

        uint h2 = 0;
        for (int i = 1; i < length - 2; i++)
        {
            h2 = unchecked((h2 * Multiplier) + root[i]);
        }

        uint h3 = 0;
        foreach (byte c in extension)
        {
            h3 = unchecked((h3 * Multiplier) + c);
        }

        return ((ulong)unchecked(h2 + h3) << 32) | h1;
    }

    private static uint ExtensionBits(ReadOnlySpan<byte> extension)
    {
        if (extension.SequenceEqual(".kf"u8))
        {
            return 0x80;
        }

        if (extension.SequenceEqual(".nif"u8))
        {
            return 0x8000;
        }

        if (extension.SequenceEqual(".dds"u8))
        {
            return 0x8080;
        }

        if (extension.SequenceEqual(".wav"u8))
        {
            return 0x80000000;
        }

        return 0;
    }
}
