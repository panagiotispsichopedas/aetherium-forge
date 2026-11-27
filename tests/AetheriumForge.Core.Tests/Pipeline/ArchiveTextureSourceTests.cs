using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Tests.Archives;

namespace AetheriumForge.Core.Tests.Pipeline;

public sealed class ArchiveTextureSourceTests : IDisposable
{
    private const string ArchiveName = "Test - Textures.bsa";
    private const string TexturePath = @"textures\clutter\a.dds";

    private static readonly byte[] TextureBytes = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];

    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), $"af-src-{Guid.NewGuid():N}");

    public ArchiveTextureSourceTests() => Directory.CreateDirectory(_dataFolder);

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ExistingEntry_ReturnsBytes()
    {
        // Arrange
        WriteArchive();
        using var source = new ArchiveTextureSource(_dataFolder);

        // Act
        var bytes = await source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TextureBytes, bytes);
    }

    [Fact]
    public async Task ReadAsync_MissingArchive_ReturnsFaultedTaskInsteadOfThrowing()
    {
        // Arrange
        using var source = new ArchiveTextureSource(_dataFolder);

        // Act
        var read = source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => read);
    }

    [Fact]
    public async Task ReadAsync_MissingEntry_ReturnsFaultedTaskInsteadOfThrowing()
    {
        // Arrange
        WriteArchive();
        using var source = new ArchiveTextureSource(_dataFolder);

        // Act
        var read = source.ReadAsync(@"textures\clutter\missing.dds", ArchiveName, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => read);
    }

    [Fact]
    public async Task ReadAsync_OpenFailedEarlier_RetriesOpen()
    {
        // Arrange: the first read fails because the archive is not there yet (a transient error).
        using var source = new ArchiveTextureSource(_dataFolder);
        await Record.ExceptionAsync(() => source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken));
        WriteArchive();

        // Act
        var bytes = await source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TextureBytes, bytes);
    }

    [Fact]
    public async Task ReadAsync_AfterDispose_ReturnsFaultedObjectDisposed()
    {
        // Arrange
        WriteArchive();
        var source = new ArchiveTextureSource(_dataFolder);
        source.Dispose();

        // Act
        var read = source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
    }

    [Fact]
    public async Task Dispose_AfterRead_ReleasesArchiveFile()
    {
        // Arrange
        var archivePath = WriteArchive();
        var source = new ArchiveTextureSource(_dataFolder);
        await source.ReadAsync(TexturePath, ArchiveName, TestContext.Current.CancellationToken);

        // Act
        source.Dispose();

        // Assert: the reader's handle is closed, so the archive can be deleted.
        File.Delete(archivePath);
        Assert.False(File.Exists(archivePath));
    }

    private string WriteArchive() => new TestBsaBuilder().Add(TexturePath, TextureBytes).WriteTo(_dataFolder, ArchiveName);
}
