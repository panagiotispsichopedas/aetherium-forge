using AetheriumForge.Core.Concurrency;

namespace AetheriumForge.Core.Tests.Concurrency;

public sealed class GpuWorkLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AetheriumForge.Tests", "GpuWorkLock", Guid.NewGuid().ToString("N"));
    private readonly string _mutexName = $@"Local\AetheriumForge.Tests.GpuWork.{Guid.NewGuid():N}";

    private string OwnerFile => Path.Combine(_folder, "gpu-work.owner");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }

    [Fact]
    public async Task AcquireAsync_Free_WritesOwnerFileWithPurpose()
    {
        var gpu = CreateLock("Aetherium Forge");

        using var handle = await gpu.AcquireAsync("a calibration", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(("a calibration", Environment.ProcessId), (gpu.ReadOwner()?.Purpose, gpu.ReadOwner()?.ProcessId));
    }

    [Fact]
    public async Task AcquireAsync_HeldElsewhere_TimesOutNamingHolder()
    {
        var app = CreateLock("Aetherium Forge");
        var cli = CreateLock("aetherium-forge forge");
        using var held = await app.AcquireAsync("a calibration", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<GpuBusyException>(() => cli.AcquireAsync("a forge run", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.Equal($"The GPU is busy with a calibration in Aetherium Forge (PID {Environment.ProcessId}).", ex.Message);
    }

    [Fact]
    public async Task Dispose_Handle_DeletesOwnerFile()
    {
        var gpu = CreateLock("test");
        var handle = await gpu.AcquireAsync("work", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        handle.Dispose();

        Assert.False(File.Exists(OwnerFile));
    }

    [Fact]
    public async Task Dispose_Handle_ReleasesForNextHolder()
    {
        var gpu = CreateLock("test");
        (await gpu.AcquireAsync("first", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();

        var taken = gpu.TryAcquire("second", out var handle, out _);
        handle?.Dispose();

        Assert.True(taken);
    }

    [Fact]
    public async Task AcquireAsync_ReleasedWhileWaiting_Acquires()
    {
        var gpu = CreateLock("test");
        var first = await gpu.AcquireAsync("first", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var waiting = gpu.AcquireAsync("second", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("second", gpu.ReadOwner()?.Purpose);
    }

    [Fact]
    public async Task AcquireAsync_CanceledWhileWaiting_ThrowsOperationCanceled()
    {
        var gpu = CreateLock("test");
        using var held = await gpu.AcquireAsync("first", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gpu.AcquireAsync("second", Timeout.InfiniteTimeSpan, cts.Token));
    }

    [Fact]
    public async Task TryAcquire_Held_ReturnsFalseWithHolder()
    {
        var gpu = CreateLock("Aetherium Forge");
        using var held = await gpu.AcquireAsync("a calibration", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var taken = gpu.TryAcquire("a forge run", out var handle, out var holder);

        Assert.Equal((false, (IDisposable?)null, "a calibration"), (taken, handle, holder?.Purpose));
    }

    [Fact]
    public void AcquireAsync_NegativeTimeout_Throws()
    {
        var gpu = CreateLock("test");

        var ex = Record.Exception(() => { _ = gpu.AcquireAsync("work", TimeSpan.FromSeconds(-2), CancellationToken.None); });

        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public async Task TryAcquire_HeldByChildProcess_ReturnsFalseNamingChild()
    {
        Directory.CreateDirectory(_folder);
        using var child = await ChildHolder.HoldMutexAsync(_mutexName, OwnerFile);
        var gpu = CreateLock("test");

        var taken = gpu.TryAcquire("a forge run", out var handle, out var holder);
        handle?.Dispose();

        Assert.Equal((false, child.ProcessId), (taken, holder?.ProcessId));
    }

    [Fact]
    public async Task AcquireAsync_ChildProcessReleases_Acquires()
    {
        using var child = await ChildHolder.HoldMutexAsync(_mutexName);
        var gpu = CreateLock("test");
        var waiting = gpu.AcquireAsync("work", TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        await child.ReleaseAsync();
        using var handle = await waiting.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task AcquireAsync_ChildDiedHoldingMutex_TakesAbandonedMutex()
    {
        using var child = await ChildHolder.HoldMutexAsync(_mutexName);
        await child.KillAsync();
        var gpu = CreateLock("test");

        using var handle = await gpu.AcquireAsync("work", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(Environment.ProcessId, gpu.ReadOwner()?.ProcessId);
    }

    [Fact]
    public void FormatMessage_UnknownOwner_IsGeneric()
    {
        Assert.Equal("The GPU is busy with another Aetherium Forge job.", GpuBusyException.FormatMessage(null));
    }

    private GpuWorkLock CreateLock(string owner) => new(owner, _mutexName, OwnerFile, logger: null);
}
