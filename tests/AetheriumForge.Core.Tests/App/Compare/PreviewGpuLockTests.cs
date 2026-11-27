using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Concurrency;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class PreviewGpuLockTests
{
    private static PreviewRequest Request(TextureKind kind = TextureKind.Diffuse) =>
        new("textures\\clutter\\a.dds", "Skyrim - Textures0.bsa", kind, 16, 16, "fake");

    private static PreviewEngine Engine(CompareFakeBackend backend, FakeGate gate) =>
        new(backend, () => PipelineState.Idle, PreviewMemoryCache.DefaultCapacityBytes, LookRenderer.Render, null, gate);

    [Fact]
    public async Task GetSampleAsync_GpuHeldElsewhere_NamesTheHolderAndDoesNotUpscale()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        var gate = new FakeGate { BusyMessage = "The GPU is busy with a calibration in Aetherium Forge (PID 42)." };
        using var engine = Engine(backend, gate);

        // Act
        var result = await engine.GetSampleAsync(Request(), null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((false, gate.BusyMessage, 0), (result.IsAvailable, result.UnavailableReason, backend.Upscaler.Calls));
    }

    [Fact]
    public async Task GetSampleAsync_GpuFree_HoldsTheLockDuringTheUpscaleAndReleasesIt()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        var gate = new FakeGate();
        using var engine = Engine(backend, gate);

        // Act
        await engine.GetSampleAsync(Request(), null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((1, 0, PreviewEngine.GpuPurpose), (gate.Acquired, gate.Held, gate.LastPurpose));
    }

    [Fact]
    public async Task GetSampleAsync_LanczosOnlyTexture_DoesNotTakeTheGpuLock()
    {
        // Arrange
        var gate = new FakeGate { BusyMessage = "busy" };
        using var engine = Engine(new CompareFakeBackend(), gate);

        // Act
        var result = await engine.GetSampleAsync(Request(TextureKind.Parallax), null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((true, 0), (result.IsAvailable, gate.Attempts));
    }

    [Fact]
    public void GpuWorkLockGate_Busy_FormatsTheHolder()
    {
        // Arrange
        var owner = new LockOwnerInfo(42, "AetheriumForge", "Aetherium Forge", DateTimeOffset.UnixEpoch, "a calibration");

        // Act
        var message = GpuBusyException.FormatMessage(owner);

        // Assert
        Assert.Equal("The GPU is busy with a calibration in Aetherium Forge (PID 42).", message);
    }

    private sealed class FakeGate : IPreviewGpuGate
    {
        public string? BusyMessage { get; init; }

        public int Attempts { get; private set; }

        public int Acquired { get; private set; }

        public int Held { get; private set; }

        public string? LastPurpose { get; private set; }

        public bool TryAcquire(string purpose, out IDisposable? handle, out string? busyMessage)
        {
            Attempts++;
            LastPurpose = purpose;
            if (BusyMessage is not null)
            {
                handle = null;
                busyMessage = BusyMessage;
                return false;
            }

            Acquired++;
            Held++;
            handle = new Release(this);
            busyMessage = null;
            return true;
        }

        private sealed class Release(FakeGate owner) : IDisposable
        {
            public void Dispose() => owner.Held--;
        }
    }
}
