using AetheriumForge.Core.Archives;

namespace AetheriumForge.Core.Tests.Archives;

public sealed class BsaReaderTests : IDisposable
{
    private const uint DefaultFlags = BsaReader.FlagFolderNames | BsaReader.FlagFileNames | BsaReader.FlagCompressed;
    private const uint UncompressedFlags = BsaReader.FlagFolderNames | BsaReader.FlagFileNames;

    private readonly string _directory = Directory.CreateTempSubdirectory("af-bsa-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(DefaultFlags)]
    [InlineData(UncompressedFlags)]
    [InlineData(DefaultFlags | BsaReader.FlagEmbeddedFileNames)]
    [InlineData(UncompressedFlags | BsaReader.FlagEmbeddedFileNames)]
    public void ReadFile_EveryEntry_ReturnsOriginalBytes(uint archiveFlags)
    {
        // Arrange
        Dictionary<string, byte[]> files = SampleFiles();
        var builder = new TestBsaBuilder(archiveFlags);
        foreach ((string path, byte[] data) in files)
        {
            builder.Add(path, data);
        }

        using BsaReader reader = BsaReader.Open(builder.WriteTo(_directory));

        // Act
        Dictionary<string, byte[]> read = reader.Entries.ToDictionary(e => e.Path, reader.ReadFile);

        // Assert
        Assert.Equal(files, read);
    }

    [Fact]
    public void Open_ValidArchive_ExposesHeaderFields()
    {
        // Arrange
        string path = new TestBsaBuilder(DefaultFlags | BsaReader.FlagEmbeddedFileNames) { ContentFlags = 0x2 }
            .Add(@"textures\a.dds", Pattern(10, 1))
            .WriteTo(_directory);

        // Act
        using BsaReader reader = BsaReader.Open(path);

        // Assert
        Assert.Equal((Path.GetFullPath(path), DefaultFlags | BsaReader.FlagEmbeddedFileNames, (ushort)0x2), (reader.ArchivePath, reader.ArchiveFlags, reader.ContentFlags));
    }

    [Fact]
    public void Open_MixedCaseStoredNames_ReturnsCanonicalPaths()
    {
        // Arrange
        string path = new TestBsaBuilder()
            .Add(@"Textures\Clutter\Bucket01.DDS", Pattern(64, 2))
            .Add(@"Textures\Clutter\Bucket01_n.dds", Pattern(64, 3))
            .WriteTo(_directory);

        // Act
        using BsaReader reader = BsaReader.Open(path);

        // Assert
        Assert.Equal([@"textures\clutter\bucket01.dds", @"textures\clutter\bucket01_n.dds"], reader.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Open_Entries_CarryStoredHashesAndRecordOrder()
    {
        // Arrange
        string path = new TestBsaBuilder()
            .Add(@"textures\clutter\b.dds", Pattern(32, 4))
            .Add(@"textures\clutter\a.dds", Pattern(32, 5))
            .Add(@"textures\actors\c.dds", Pattern(32, 6))
            .WriteTo(_directory);

        // Act
        using BsaReader reader = BsaReader.Open(path);

        // Assert: folders sorted by folder hash, files by file hash, as written.
        Assert.Equal(reader.Entries.OrderBy(e => e.FolderHash).ThenBy(e => e.FileHash), reader.Entries);
    }

    [Fact]
    public void Open_ToggleBitInCompressedArchive_EntryIsUncompressedAndSizeHasNoToggleBit()
    {
        // Arrange
        byte[] data = Pattern(500, 7);
        string path = new TestBsaBuilder(DefaultFlags).Add(@"textures\raw.dds", data, toggleCompression: true).WriteTo(_directory);

        // Act
        using BsaReader reader = BsaReader.Open(path);
        BsaEntry entry = Assert.Single(reader.Entries);

        // Assert
        Assert.Equal((false, (uint)data.Length), (entry.IsCompressed, entry.Size));
    }

    [Fact]
    public void Open_ToggleBitInUncompressedArchive_EntryIsCompressed()
    {
        // Arrange
        string path = new TestBsaBuilder(UncompressedFlags).Add(@"textures\packed.dds", Pattern(500, 8), toggleCompression: true).WriteTo(_directory);

        // Act
        using BsaReader reader = BsaReader.Open(path);

        // Assert
        Assert.True(Assert.Single(reader.Entries).IsCompressed);
    }

    [Theory]
    [InlineData(DefaultFlags)]
    [InlineData(UncompressedFlags)]
    [InlineData(DefaultFlags | BsaReader.FlagEmbeddedFileNames)]
    public void ReadFile_ToggledFile_ReturnsOriginalBytes(uint archiveFlags)
    {
        // Arrange
        byte[] toggled = Pattern(3000, 9);
        byte[] normal = Pattern(3000, 10);
        string path = new TestBsaBuilder(archiveFlags)
            .Add(@"textures\toggled.dds", toggled, toggleCompression: true)
            .Add(@"textures\normal.dds", normal)
            .WriteTo(_directory);
        using BsaReader reader = BsaReader.Open(path);

        // Act
        byte[] read = reader.ReadFile(reader.Entries.Single(e => e.Path == @"textures\toggled.dds"));

        // Assert
        Assert.Equal(toggled, read);
    }

    [Theory]
    [InlineData(DefaultFlags)]
    [InlineData(UncompressedFlags)]
    [InlineData(DefaultFlags | BsaReader.FlagEmbeddedFileNames)]
    [InlineData(UncompressedFlags | BsaReader.FlagEmbeddedFileNames)]
    public void ReadPrefix_DestinationShorterThanFile_ReturnsLeadingBytes(uint archiveFlags)
    {
        // Arrange: 300 KB spans several 64 KB LZ4 blocks, so only the first block has to be decoded.
        byte[] data = Pattern(300 * 1024, 11);
        string path = new TestBsaBuilder(archiveFlags).Add(@"textures\big.dds", data).WriteTo(_directory);
        using BsaReader reader = BsaReader.Open(path);
        byte[] prefix = new byte[148];

        // Act
        int written = reader.ReadPrefix(reader.Entries[0], prefix);

        // Assert
        Assert.Equal(data.AsSpan(0, 148).ToArray(), prefix.AsSpan(0, written).ToArray());
    }

    [Theory]
    [InlineData(DefaultFlags)]
    [InlineData(UncompressedFlags | BsaReader.FlagEmbeddedFileNames)]
    public void ReadPrefix_DestinationLongerThanFile_ReturnsWholeFileLength(uint archiveFlags)
    {
        // Arrange
        byte[] data = Pattern(100, 12);
        string path = new TestBsaBuilder(archiveFlags).Add(@"textures\small.dds", data).WriteTo(_directory);
        using BsaReader reader = BsaReader.Open(path);
        byte[] prefix = new byte[148];

        // Act
        int written = reader.ReadPrefix(reader.Entries[0], prefix);

        // Assert
        Assert.Equal(data, prefix.AsSpan(0, written).ToArray());
    }

    [Fact]
    public void ReadFile_ConcurrentCalls_ReturnOriginalBytes()
    {
        // Arrange
        var files = Enumerable.Range(0, 64).ToDictionary(i => $@"textures\folder{i % 5}\file{i}.dds", i => Pattern(20_000 + (i * 37), i));
        var builder = new TestBsaBuilder(DefaultFlags | BsaReader.FlagEmbeddedFileNames);
        foreach ((string path, byte[] data) in files)
        {
            builder.Add(path, data);
        }

        using BsaReader reader = BsaReader.Open(builder.WriteTo(_directory));
        BsaEntry[] work = Enumerable.Repeat(reader.Entries, 8).SelectMany(e => e).ToArray();
        var results = new byte[work.Length][];

        // Act
        Parallel.For(0, work.Length, i => results[i] = reader.ReadFile(work[i]));

        // Assert
        Assert.All(Enumerable.Range(0, work.Length), i => Assert.Equal(files[work[i].Path], results[i]));
    }

    [Fact]
    public void FindHashMismatches_ConsistentArchive_ReturnsEmpty()
    {
        // Arrange
        var builder = new TestBsaBuilder();
        foreach ((string path, byte[] data) in SampleFiles())
        {
            builder.Add(path, data);
        }

        using BsaReader reader = BsaReader.Open(builder.WriteTo(_directory));

        // Act
        IReadOnlyList<BsaEntry> mismatches = reader.FindHashMismatches();

        // Assert
        Assert.Empty(mismatches);
    }

    [Fact]
    public void FindHashMismatches_CorruptFileHash_ReturnsThatEntry()
    {
        // Arrange
        string path = new TestBsaBuilder()
            .Add(@"textures\good.dds", Pattern(16, 13))
            .Add(@"textures\bad.dds", Pattern(16, 14), fileHashOverride: 0x1234)
            .WriteTo(_directory);
        using BsaReader reader = BsaReader.Open(path);

        // Act
        IReadOnlyList<BsaEntry> mismatches = reader.FindHashMismatches();

        // Assert
        Assert.Equal(@"textures\bad.dds", Assert.Single(mismatches).Path);
    }

    [Fact]
    public void Open_BadMagic_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] bytes = new TestBsaBuilder().Add(@"textures\a.dds", Pattern(8, 15)).Build();
        bytes[0] = (byte)'X';
        string path = Path.Combine(_directory, "badmagic.bsa");
        File.WriteAllBytes(path, bytes);

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => BsaReader.Open(path));
    }

    [Theory]
    [InlineData(103u)]
    [InlineData(104u)]
    [InlineData(106u)]
    public void Open_UnsupportedVersion_ThrowsInvalidDataException(uint version)
    {
        // Arrange
        string path = new TestBsaBuilder { Version = version }.Add(@"textures\a.dds", Pattern(8, 16)).WriteTo(_directory);

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => BsaReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedFile_ThrowsInvalidDataException()
    {
        // Arrange
        string path = Path.Combine(_directory, "short.bsa");
        File.WriteAllBytes(path, "BSA\0"u8.ToArray());

        // Act + Assert
        Assert.Throws<InvalidDataException>(() => BsaReader.Open(path));
    }

    [Fact]
    public void ReadFile_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        string path = new TestBsaBuilder().Add(@"textures\a.dds", Pattern(8, 17)).WriteTo(_directory);
        BsaReader reader = BsaReader.Open(path);
        BsaEntry entry = reader.Entries[0];
        reader.Dispose();

        // Act + Assert
        Assert.Throws<ObjectDisposedException>(() => reader.ReadFile(entry));
    }

    [Fact]
    public void Open_NullPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => BsaReader.Open(null!));
    }

    private static Dictionary<string, byte[]> SampleFiles() => new()
    {
        [@"textures\clutter\bucket01.dds"] = Pattern(5000, 20),
        [@"textures\clutter\bucket01_n.dds"] = Pattern(4096, 21),
        [@"textures\actors\dog\dog.dds"] = Pattern(70_000, 22),
        [@"textures\empty.dds"] = [],
        [@"textures\tiny.dds"] = [0x42],
    };

    /// <summary>Deterministic, partly compressible bytes that differ by <paramref name="seed"/>.</summary>
    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        uint state = (uint)seed * 2654435761u;
        for (int i = 0; i < data.Length; i++)
        {
            state = (state * 1103515245u) + 12345u;
            data[i] = (i % 4 == 0) ? (byte)(state >> 24) : (byte)(i / 64);
        }

        return data;
    }
}
