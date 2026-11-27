using AetheriumForge.Core.Concurrency;

namespace AetheriumForge.Core.Tests.Concurrency;

public sealed class ProjectLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AetheriumForge.Tests", "ProjectLock", Guid.NewGuid().ToString("N"));

    public ProjectLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }

    [Fact]
    public void Acquire_FreeProject_WritesOwnerInfo()
    {
        using var projectLock = ProjectLock.Acquire(_root, "aetherium-forge scan");

        var owner = ProjectLock.ReadOwner(_root);

        Assert.Equal((Environment.ProcessId, "aetherium-forge scan"), (owner?.ProcessId, owner?.Description));
    }

    [Fact]
    public void Acquire_FreeProject_CreatesLockFileInProjectRoot()
    {
        using var projectLock = ProjectLock.Acquire(_root, "test");

        Assert.Equal(Path.Combine(_root, ".aetherium.lock"), projectLock.LockPath);
    }

    [Fact]
    public void Acquire_AlreadyHeldInProcess_ThrowsWithOwner()
    {
        using var first = ProjectLock.Acquire(_root, "Aetherium Forge");

        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(_root, "aetherium-forge forge"));

        Assert.Equal($"Project is open in Aetherium Forge (PID {Environment.ProcessId}). Close it there first.", ex.Message);
    }

    [Fact]
    public void TryAcquire_AlreadyHeld_ReturnsFalseWithHolder()
    {
        using var first = ProjectLock.Acquire(_root, "Aetherium Forge");

        var taken = ProjectLock.TryAcquire(_root, "second", out var second, out var holder);

        Assert.Equal((false, (ProjectLock?)null, Environment.ProcessId), (taken, second, holder?.ProcessId));
    }

    [Fact]
    public void Dispose_HeldLock_DeletesLockFile()
    {
        var projectLock = ProjectLock.Acquire(_root, "test");

        projectLock.Dispose();

        Assert.False(File.Exists(ProjectLock.GetLockPath(_root)));
    }

    [Fact]
    public void Dispose_HeldLock_AllowsReacquire()
    {
        ProjectLock.Acquire(_root, "first").Dispose();

        var taken = ProjectLock.TryAcquire(_root, "second", out var again, out _);
        again?.Dispose();

        Assert.True(taken);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var projectLock = ProjectLock.Acquire(_root, "test");
        projectLock.Dispose();

        var ex = Record.Exception(projectLock.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void TryAcquire_StaleLockFileLeftBehind_TakesLockAndOverwritesOwner()
    {
        File.WriteAllText(ProjectLock.GetLockPath(_root), "pid=999999\nprocess=dead\nowner=crashed app\nstarted=2020-01-01T00:00:00Z\n");

        using var projectLock = ProjectLock.Acquire(_root, "fresh");

        Assert.Equal("fresh", ProjectLock.ReadOwner(_root)?.Description);
    }

    [Fact]
    public void Acquire_MissingFolder_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() => ProjectLock.Acquire(missing, "test"));
    }

    [Fact]
    public void ReadOwner_NotLocked_ReturnsNull()
    {
        Assert.Null(ProjectLock.ReadOwner(_root));
    }

    [Theory]
    [InlineData(@"C:\p\.aetherium.lock", true)]
    [InlineData(".AETHERIUM.LOCK", true)]
    [InlineData(@"C:\p\forge.db", false)]
    public void IsLockFile_Path_RecognisesLockFile(string path, bool expected)
    {
        Assert.Equal(expected, ProjectLock.IsLockFile(path));
    }

    [Fact]
    public void FormatMessage_UnknownOwner_NamesNoPid()
    {
        Assert.Equal("Project is open in another Aetherium Forge process. Close it there first.", ProjectLockedException.FormatMessage(null));
    }

    [Fact]
    public async Task Acquire_HeldByChildProcess_ThrowsNamingChildPid()
    {
        using var child = await ChildHolder.HoldProjectLockAsync(ProjectLock.GetLockPath(_root));

        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(_root, "test"));

        Assert.Equal(child.ProcessId, ex.Owner?.ProcessId);
    }

    [Fact]
    public async Task Acquire_ChildProcessReleased_Succeeds()
    {
        using var child = await ChildHolder.HoldProjectLockAsync(ProjectLock.GetLockPath(_root));
        await child.ReleaseAsync();

        var taken = ProjectLock.TryAcquire(_root, "test", out var projectLock, out _);
        projectLock?.Dispose();

        Assert.True(taken);
    }

    [Fact]
    public async Task Acquire_HolderProcessKilled_StaleLockDoesNotBlock()
    {
        using var child = await ChildHolder.HoldProjectLockAsync(ProjectLock.GetLockPath(_root));
        await child.KillAsync();

        var taken = ProjectLock.TryAcquire(_root, "test", out var projectLock, out _);
        projectLock?.Dispose();

        Assert.True(taken);
    }

    [Fact]
    public async Task Acquire_HeldHere_ChildProcessCannotOpenForWrite()
    {
        using var projectLock = ProjectLock.Acquire(_root, "test");
        var path = ChildHolder.Escape(projectLock.LockPath);

        var result = await ChildHolder.RunAsync(
            $"try {{ $fs = New-Object IO.FileStream('{path}', 'OpenOrCreate', 'ReadWrite', 'Read'); $fs.Dispose(); 'OPENED' }} catch {{ 'LOCKED' }}");

        Assert.Equal("LOCKED", result);
    }
}
