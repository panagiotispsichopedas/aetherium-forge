using System.Buffers.Binary;
using System.Text;
using AetheriumForge.Core.Common;
using K4os.Compression.LZ4.Streams;
using Microsoft.Win32.SafeHandles;

namespace AetheriumForge.Core.Archives;

/// <summary>
/// Reads Skyrim SE (version 105) BSA archives (IMPLEMENTATION_GUIDE §6).
/// </summary>
/// <remarks>
/// The directory (folder and file records plus names) is parsed once by <see cref="Open"/>. File data is read with
/// positional reads on a shared handle, so <see cref="ReadFile"/>, <see cref="ReadPrefix"/> and
/// <see cref="FindHashMismatches"/> are safe to call concurrently from many threads.
/// </remarks>
public sealed class BsaReader : IDisposable
{
    /// <summary>The only archive version this reader accepts (Skyrim Special Edition).</summary>
    public const uint SupportedVersion = 105;

    /// <summary>Archive flag: folder names are stored in the folder blocks.</summary>
    public const uint FlagFolderNames = 0x1;

    /// <summary>Archive flag: file names are stored in the file-name block.</summary>
    public const uint FlagFileNames = 0x2;

    /// <summary>Archive flag: files are compressed unless their size carries <see cref="CompressionToggleBit"/>.</summary>
    public const uint FlagCompressed = 0x4;

    /// <summary>Archive flag: every data block starts with the file's full path as a length-prefixed string.</summary>
    public const uint FlagEmbeddedFileNames = 0x100;

    /// <summary>Bit in a file record's size that inverts the archive's default compression for that file.</summary>
    public const uint CompressionToggleBit = 0x40000000;

    private const int HeaderSize = 36;
    private const int FolderRecordSize = 24;
    private const int FileRecordSize = 16;
    private const int MetadataBufferSize = 64 * 1024;
    private const int StreamBufferSize = 64 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly long _archiveLength;

    private BsaReader(string archivePath, SafeFileHandle handle, long archiveLength, uint archiveFlags, ushort contentFlags, IReadOnlyList<BsaEntry> entries)
    {
        ArchivePath = archivePath;
        _handle = handle;
        _archiveLength = archiveLength;
        ArchiveFlags = archiveFlags;
        ContentFlags = contentFlags;
        Entries = entries;
    }

    /// <summary>Gets the full path of the archive file.</summary>
    public string ArchivePath { get; }

    /// <summary>Gets the archive flags from the header (see the <c>Flag*</c> constants).</summary>
    public uint ArchiveFlags { get; }

    /// <summary>Gets the content flags from the header (textures = <c>0x2</c>).</summary>
    public ushort ContentFlags { get; }

    /// <summary>Gets every file record in archive order (folders in record order, files in record order within each folder).</summary>
    public IReadOnlyList<BsaEntry> Entries { get; }

    /// <summary>
    /// Opens an archive and parses its directory.
    /// </summary>
    /// <param name="archivePath">Path of the <c>.bsa</c> file.</param>
    /// <returns>A reader that owns an open handle to the archive. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="archivePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The file is not a version 105 BSA, lacks folder or file names, or its directory is malformed.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static BsaReader Open(string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        string fullPath = Path.GetFullPath(archivePath);
        SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            long length = RandomAccess.GetLength(handle);
            (uint archiveFlags, ushort contentFlags, List<BsaEntry> entries) = ReadDirectory(handle, length);

            return new BsaReader(fullPath, handle, length, archiveFlags, contentFlags, entries.AsReadOnly());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads and, if needed, decompresses the full contents of a file.
    /// </summary>
    /// <param name="entry">An entry from <see cref="Entries"/>.</param>
    /// <returns>The file's decompressed bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The data block is out of range or does not decompress to its declared size.</exception>
    public byte[] ReadFile(BsaEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        DataBlock block = LocateData(entry);
        if (!block.IsCompressed)
        {
            byte[] raw = new byte[block.Length];
            ReadExactly(block.Start, raw);

            return raw;
        }

        byte[] output = new byte[block.OriginalSize];
        using LZ4DecoderStream decoder = OpenDecoder(block);
        int read = decoder.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
        if (read != output.Length)
        {
            throw new InvalidDataException($"'{entry.Path}' in '{ArchivePath}' decompressed to {read} bytes, expected {output.Length}.");
        }

        return output;
    }

    /// <summary>
    /// Reads only the first bytes of a file, decoding just as much of the LZ4 frame as needed.
    /// </summary>
    /// <param name="entry">An entry from <see cref="Entries"/>.</param>
    /// <param name="destination">Receives up to <c>destination.Length</c> decompressed bytes from the start of the file.</param>
    /// <returns>The number of bytes written, which is less than <c>destination.Length</c> only when the file is shorter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The data block is out of range or malformed.</exception>
    public int ReadPrefix(BsaEntry entry, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        DataBlock block = LocateData(entry);
        if (!block.IsCompressed)
        {
            Span<byte> target = destination[..(int)Math.Min(destination.Length, block.Length)];
            ReadExactly(block.Start, target);

            return target.Length;
        }

        Span<byte> wanted = destination[..(int)Math.Min(destination.Length, block.OriginalSize)];
        using LZ4DecoderStream decoder = OpenDecoder(block);

        return decoder.ReadAtLeast(wanted, wanted.Length, throwOnEndOfStream: false);
    }

    /// <summary>
    /// Recomputes the folder and file hash of every entry from its path and returns the entries that disagree with the stored hashes.
    /// </summary>
    /// <returns>The mismatching entries; empty when every stored hash matches.</returns>
    public IReadOnlyList<BsaEntry> FindHashMismatches()
    {
        List<BsaEntry> mismatches = [];
        foreach (BsaEntry entry in Entries)
        {
            int separator = entry.Path.LastIndexOf('\\');
            string folder = separator < 0 ? string.Empty : entry.Path[..separator];
            string file = entry.Path[(separator + 1)..];
            if (TesHash.HashFolder(folder) != entry.FolderHash || TesHash.HashFile(file) != entry.FileHash)
            {
                mismatches.Add(entry);
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Closes the archive handle. Subsequent reads throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose() => _handle.Dispose();

    private static (uint ArchiveFlags, ushort ContentFlags, List<BsaEntry> Entries) ReadDirectory(SafeFileHandle handle, long length)
    {
        using var region = new ArchiveRegionStream(handle, 0, length);
        using var buffered = new BufferedStream(region, MetadataBufferSize);
        using var reader = new BinaryReader(buffered, Encoding.Latin1, leaveOpen: true);

        if (length < HeaderSize)
        {
            throw new InvalidDataException("File is too small to be a BSA archive.");
        }

        if (reader.ReadUInt32() != 0x00415342)
        {
            throw new InvalidDataException("Missing 'BSA\\0' magic.");
        }

        uint version = reader.ReadUInt32();
        if (version != SupportedVersion)
        {
            throw new InvalidDataException($"Unsupported BSA version {version}; only {SupportedVersion} (Skyrim SE) is supported.");
        }

        uint folderRecordsOffset = reader.ReadUInt32();
        uint archiveFlags = reader.ReadUInt32();
        uint folderCount = reader.ReadUInt32();
        uint fileCount = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // total folder-name length; folder blocks are located through their record offsets instead.
        uint totalFileNameLength = reader.ReadUInt32();
        ushort contentFlags = reader.ReadUInt16();

        if ((archiveFlags & (FlagFolderNames | FlagFileNames)) != (FlagFolderNames | FlagFileNames))
        {
            throw new InvalidDataException($"Archive flags 0x{archiveFlags:X} lack folder or file names; such archives are not supported.");
        }

        if (folderRecordsOffset + ((long)folderCount * FolderRecordSize) > length || (long)fileCount * FileRecordSize > length)
        {
            throw new InvalidDataException("Folder or file counts exceed the archive size.");
        }

        buffered.Seek(folderRecordsOffset, SeekOrigin.Begin);
        var folders = new (ulong Hash, uint FileCount, long BlockOffset)[folderCount];
        for (int i = 0; i < folders.Length; i++)
        {
            ulong hash = reader.ReadUInt64();
            uint count = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            ulong offset = reader.ReadUInt64();
            folders[i] = (hash, count, (long)offset - totalFileNameLength);
        }

        var records = new List<(string Folder, ulong FolderHash, ulong FileHash, uint RawSize, uint Offset)>((int)fileCount);
        long fileNamesOffset = folderRecordsOffset + ((long)folderCount * FolderRecordSize);
        foreach ((ulong folderHash, uint count, long blockOffset) in folders)
        {
            if (blockOffset < 0 || blockOffset >= length)
            {
                throw new InvalidDataException($"Folder block offset {blockOffset} is outside the archive.");
            }

            buffered.Seek(blockOffset, SeekOrigin.Begin);
            byte nameLength = reader.ReadByte();
            string folder = Encoding.Latin1.GetString(reader.ReadBytes(nameLength)).TrimEnd('\0');
            for (uint f = 0; f < count; f++)
            {
                records.Add((folder, folderHash, reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32()));
            }

            fileNamesOffset = Math.Max(fileNamesOffset, buffered.Position);
        }

        if (records.Count != fileCount)
        {
            throw new InvalidDataException($"Folder records list {records.Count} files but the header declares {fileCount}.");
        }

        if (fileNamesOffset + totalFileNameLength > length)
        {
            throw new InvalidDataException("File-name block extends past the end of the archive.");
        }

        buffered.Seek(fileNamesOffset, SeekOrigin.Begin);
        byte[] names = reader.ReadBytes((int)totalFileNameLength);
        bool compressedByDefault = (archiveFlags & FlagCompressed) != 0;
        var entries = new List<BsaEntry>(records.Count);
        int nameStart = 0;
        foreach ((string folder, ulong folderHash, ulong fileHash, uint rawSize, uint offset) in records)
        {
            int nameEnd = Array.IndexOf(names, (byte)0, nameStart);
            if (nameEnd < 0)
            {
                throw new InvalidDataException("File-name block holds fewer names than file records.");
            }

            string fileName = Encoding.Latin1.GetString(names, nameStart, nameEnd - nameStart);
            nameStart = nameEnd + 1;

            bool isCompressed = compressedByDefault ^ ((rawSize & CompressionToggleBit) != 0);
            string path = GamePath.Normalize(folder.Length == 0 ? fileName : $"{folder}\\{fileName}");
            entries.Add(new BsaEntry(path, folderHash, fileHash, rawSize & ~CompressionToggleBit, offset, isCompressed));
        }

        return (archiveFlags, contentFlags, entries);
    }

    private DataBlock LocateData(BsaEntry entry)
    {
        long start = entry.Offset;
        long end = start + entry.Size;
        if (end > _archiveLength)
        {
            throw new InvalidDataException($"'{entry.Path}' data block [{start}, {end}) is outside '{ArchivePath}'.");
        }

        if ((ArchiveFlags & FlagEmbeddedFileNames) != 0)
        {
            Span<byte> nameLength = stackalloc byte[1];
            ReadExactly(start, nameLength);
            start += 1 + nameLength[0];
        }

        if (!entry.IsCompressed)
        {
            return CheckedBlock(entry, start, end, isCompressed: false, originalSize: 0);
        }

        Span<byte> sizeBytes = stackalloc byte[sizeof(uint)];
        ReadExactly(start, sizeBytes);
        uint originalSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
        if (originalSize > Array.MaxLength)
        {
            throw new InvalidDataException($"'{entry.Path}' declares an original size of {originalSize} bytes, which is too large.");
        }

        return CheckedBlock(entry, start + sizeof(uint), end, isCompressed: true, originalSize);
    }

    private DataBlock CheckedBlock(BsaEntry entry, long start, long end, bool isCompressed, uint originalSize)
    {
        if (start > end)
        {
            throw new InvalidDataException($"'{entry.Path}' in '{ArchivePath}' has a data block shorter than its prefix.");
        }

        return new DataBlock(start, end - start, isCompressed, originalSize);
    }

    private LZ4DecoderStream OpenDecoder(DataBlock block)
    {
        var region = new ArchiveRegionStream(_handle, block.Start, block.Length);

        return LZ4Stream.Decode(new BufferedStream(region, StreamBufferSize), leaveOpen: false);
    }

    private void ReadExactly(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new InvalidDataException($"Unexpected end of '{ArchivePath}' at offset {offset + total}.");
            }

            total += read;
        }
    }

    private readonly record struct DataBlock(long Start, long Length, bool IsCompressed, uint OriginalSize);
}
