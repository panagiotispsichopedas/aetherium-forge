using AetheriumForge.App.Pages.Rules;

namespace AetheriumForge.Core.Tests.App.Rules;

public sealed class RulesFileIoTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));

    public RulesFileIoTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task WriteAtomicAsync_ExistingFile_ReplacesContent()
    {
        // Arrange
        string path = Path.Combine(_folder, "rules.json");
        await File.WriteAllTextAsync(path, "old old old old", TestContext.Current.CancellationToken);

        // Act
        await RulesFileIo.WriteAtomicAsync(path, "new");

        // Assert
        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAtomicAsync_Written_LeavesNoTemporaryFile()
    {
        // Act
        await RulesFileIo.WriteAtomicAsync(Path.Combine(_folder, "rules.json"), "{}");

        // Assert
        Assert.Equal(["rules.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WriteAtomicAsync_TargetIsReadOnly_ThrowsAndKeepsOldContent()
    {
        // Arrange
        string path = Path.Combine(_folder, "rules.json");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            // Act
            await Assert.ThrowsAnyAsync<Exception>(() => RulesFileIo.WriteAtomicAsync(path, "new"));

            // Assert
            Assert.Equal(("old", 1), (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), Directory.GetFiles(_folder).Length));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
