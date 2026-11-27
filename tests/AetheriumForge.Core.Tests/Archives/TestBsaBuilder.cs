using System.Text;
using AetheriumForge.Core.Archives;
using K4os.Compression.LZ4.Streams;

namespace AetheriumForge.Core.Tests.Archives;

/// <summary>
/// Builds small Skyrim SE (version 105) BSA archives in memory for reader tests.
/// </summary>
/// <remarks>
/// Layout follows IMPLEMENTATION_GUIDE §6. The header's total folder-name length counts each name plus its null but
/// not its length byte, which is what the vanilla archives store.
/// </remarks>
internal sealed class TestBsaBuilder
{
    private readonly uint _archiveFlags;
    private readonly List<PendingFile> _files = [];

    public TestBsaBuilder(uint archiveFlags = BsaReader.FlagFolderNames | BsaReader.FlagFileNames | BsaReader.FlagCompressed)
    {
        _archiveFlags = archiveFlags;
    }

    public ushort ContentFlags { get; init; } = 0x2;

    public uint Version { get; init; } = BsaReader.SupportedVersion;

    /// <summary>Gets the LZ4 frame block size used for compressed files.</summary>
    public int LZ4BlockSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Adds a file.
    /// </summary>
    /// <param name="path">Path as it should be stored, for example <c>textures\clutter\bucket01.dds</c>. Case is preserved.</param>
    /// <param name="data">The uncompressed file contents.</param>
    /// <param name="toggleCompression">Set the <c>0x40000000</c> bit so the file is stored opposite to the archive default.</param>
    /// <param name="fileHashOverride">Store this file hash instead of the computed one, to simulate a corrupt record.</param>
    public TestBsaBuilder Add(string path, byte[] data, bool toggleCompression = false, ulong? fileHashOverride = null)
    {
        string normalized = path.Replace('/', '\\');
        int separator = normalized.LastIndexOf('\\');
        _files.Add(new PendingFile(normalized[..separator], normalized[(separator + 1)..], data, toggleCompression, fileHashOverride));

        return this;
    }

    public byte[] Build()
    {
        var folders = _files
            .GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Hash: TesHash.HashFolder(g.Key), Files: g.OrderBy(f => f.FileHash).ToList()))
            .OrderBy(f => f.Hash)
            .ToList();
        List<PendingFile> orderedFiles = folders.SelectMany(f => f.Files).ToList();

        int totalFolderNameLength = folders.Sum(f => f.Name.Length + 1);
        int totalFileNameLength = orderedFiles.Sum(f => f.Name.Length + 1);
        long folderBlocksStart = 36 + (folders.Count * 24L);
        long fileNamesStart = folderBlocksStart + folders.Sum(f => 1 + f.Name.Length + 1 + (f.Files.Count * 16L));
        long dataStart = fileNamesStart + totalFileNameLength;

        bool compressedByDefault = (_archiveFlags & BsaReader.FlagCompressed) != 0;
        bool embedNames = (_archiveFlags & BsaReader.FlagEmbeddedFileNames) != 0;
        var blocks = new Dictionary<PendingFile, (byte[] Block, uint Offset)>();
        long cursor = dataStart;
        foreach (PendingFile file in orderedFiles)
        {
            byte[] block = EncodeBlock(file, compressedByDefault ^ file.ToggleCompression, embedNames);
            blocks[file] = (block, checked((uint)cursor));
            cursor += block.Length;
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.Latin1, leaveOpen: true);
        writer.Write("BSA\0"u8);
        writer.Write(Version);
        writer.Write(36u);
        writer.Write(_archiveFlags);
        writer.Write((uint)folders.Count);
        writer.Write((uint)orderedFiles.Count);
        writer.Write((uint)totalFolderNameLength);
        writer.Write((uint)totalFileNameLength);
        writer.Write(ContentFlags);
        writer.Write((ushort)0);

        long blockOffset = folderBlocksStart;
        foreach ((string name, ulong hash, List<PendingFile> files) in folders)
        {
            writer.Write(hash);
            writer.Write((uint)files.Count);
            writer.Write(0u);
            writer.Write((ulong)(blockOffset + totalFileNameLength));
            blockOffset += 1 + name.Length + 1 + (files.Count * 16L);
        }

        foreach ((string name, _, List<PendingFile> files) in folders)
        {
            writer.Write((byte)(name.Length + 1));
            writer.Write(Encoding.Latin1.GetBytes(name));
            writer.Write((byte)0);
            foreach (PendingFile file in files)
            {
                (byte[] block, uint offset) = blocks[file];
                writer.Write(file.StoredFileHash);
                writer.Write((uint)block.Length | (file.ToggleCompression ? BsaReader.CompressionToggleBit : 0u));
                writer.Write(offset);
            }
        }

        foreach (PendingFile file in orderedFiles)
        {
            writer.Write(Encoding.Latin1.GetBytes(file.Name));
            writer.Write((byte)0);
        }

        foreach (PendingFile file in orderedFiles)
        {
            writer.Write(blocks[file].Block);
        }

        writer.Flush();

        return output.ToArray();
    }

    public string WriteTo(string directory, string fileName = "test.bsa")
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build());

        return path;
    }

    private byte[] EncodeBlock(PendingFile file, bool compress, bool embedName)
    {
        using var block = new MemoryStream();
        if (embedName)
        {
            byte[] fullName = Encoding.Latin1.GetBytes($"{file.Folder}\\{file.Name}");
            block.WriteByte((byte)fullName.Length);
            block.Write(fullName);
        }

        if (!compress)
        {
            block.Write(file.Data);

            return block.ToArray();
        }

        block.Write(BitConverter.GetBytes((uint)file.Data.Length));
        using (LZ4EncoderStream encoder = LZ4Stream.Encode(block, new LZ4EncoderSettings { BlockSize = LZ4BlockSize }, leaveOpen: true))
        {
            encoder.Write(file.Data);
        }

        return block.ToArray();
    }

    private sealed record PendingFile(string Folder, string Name, byte[] Data, bool ToggleCompression, ulong? FileHashOverride)
    {
        public ulong FileHash { get; } = TesHash.HashFile(Name);

        public ulong StoredFileHash => FileHashOverride ?? FileHash;
    }
}
