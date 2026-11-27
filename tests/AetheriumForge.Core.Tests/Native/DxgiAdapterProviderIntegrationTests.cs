using System.Diagnostics;
using System.Globalization;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Tests.Imaging.Upscaling;
using AetheriumForge.Native;

namespace AetheriumForge.Core.Tests.Native;

/// <summary>Serialises the DXGI integration tests so the leak check and GPU-memory measurements are not disturbed by other tests.</summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class DxgiSerialTests
{
}

/// <summary>
/// Runs <see cref="DxgiAdapterProvider"/> against the real DXGI of this machine. Hardware-specific assertions skip on machines
/// without a hardware GPU (CI) or without the reference GTX 1080.
/// </summary>
[Collection(typeof(DxgiSerialTests))]
public sealed class DxgiAdapterProviderIntegrationTests : IDisposable
{
    private const int NvidiaVendorId = 0x10DE;
    private const ulong Gibibyte = 1024UL * 1024 * 1024;

    private readonly DxgiAdapterProvider _provider = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void GetAdapters_RealMachine_ReturnsAtLeastOneAdapter()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");

        // Act
        var adapters = _provider.GetAdapters();
        Report(adapters);

        // Assert
        Assert.NotEmpty(adapters);
    }

    [Fact]
    public void GetAdapters_RealMachine_DirectMLDeviceIdsAreDistinctEnumerationIndices()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");

        // Act
        var adapters = _provider.GetAdapters();

        // Assert: every EnumAdapters1 index 0..n-1 appears exactly once, whatever the preference order.
        Assert.Equal(Enumerable.Range(0, adapters.Count), adapters.Select(adapter => adapter.DirectMLDeviceId).Order());
    }

    [Fact]
    public void GetAdapters_RealMachine_HardwareAdaptersComeBeforeSoftware()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");

        // Act
        var adapters = _provider.GetAdapters();

        // Assert: high-performance order never puts the Basic Render Driver ahead of a hardware GPU.
        var firstSoftware = adapters.ToList().FindIndex(adapter => adapter.IsSoftware);
        var lastHardware = adapters.ToList().FindLastIndex(adapter => !adapter.IsSoftware);
        Assert.True(firstSoftware < 0 || lastHardware < firstSoftware, "A software adapter is ordered before a hardware adapter.");
    }

    [Fact]
    public void GetAdapters_ReferenceMachine_ReportsGtx1080WithAbout8GiB()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        var gtx = _provider.GetAdapters().FirstOrDefault(adapter => adapter.VendorId == NvidiaVendorId && adapter.Name.Contains("GTX 1080", StringComparison.Ordinal));
        Assert.SkipWhen(gtx is null, "The reference GeForce GTX 1080 is not present.");

        // Assert: 8 GB card; DXGI reports a little under 8 GiB dedicated.
        Assert.InRange(gtx.DedicatedVideoMemory, 7 * Gibibyte, 9 * Gibibyte);
        Assert.False(gtx.IsIntegrated || gtx.IsSoftware, "The GTX 1080 must be a discrete hardware adapter.");
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", gtx.DriverVersion ?? string.Empty);
    }

    [Fact]
    public void QueryVideoMemory_HardwareAdapter_ReturnsNonZeroBudget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        var adapter = PrimaryHardwareAdapter();
        Assert.SkipWhen(adapter is null, "No hardware GPU.");

        // Act
        var memory = _provider.QueryVideoMemory(adapter.Luid);

        // Assert
        Assert.NotNull(memory);
        Assert.True(memory.Budget > 0, "Budget must be non-zero.");
    }

    [Fact]
    public void QueryVideoMemory_UnknownLuid_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");

        // Arrange: LUIDs are allocated from small positive values; this one cannot exist.
        const long UnknownLuid = 0x7FFF_FFFF_7FFF_FFFF;

        // Act
        var memory = _provider.QueryVideoMemory(UnknownLuid);

        // Assert
        Assert.Null(memory);
    }

    [Fact]
    public void GetAdaptersAndQueryVideoMemory_10000Calls_DoNotLeakHandlesOrMemory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        const int Iterations = 10_000;
        const int HandleTolerance = 64;
        const long PrivateBytesTolerance = 16L * 1024 * 1024;

        // Arrange: warm up DXGI, the D3D12 integrated-GPU cache, the JIT and the driver.
        var luid = _provider.GetAdapters()[0].Luid;
        for (var i = 0; i < 200; i++)
        {
            _ = _provider.GetAdapters();
            _ = _provider.QueryVideoMemory(luid);
        }

        var (handlesBefore, privateBefore) = Measure();

        // Act
        for (var i = 0; i < Iterations; i++)
        {
            _ = _provider.GetAdapters();
            _ = _provider.QueryVideoMemory(luid);
        }

        var (handlesAfter, privateAfter) = Measure();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Handles {handlesBefore} -> {handlesAfter}; private bytes {privateBefore:N0} -> {privateAfter:N0} after {Iterations:N0} × (GetAdapters + QueryVideoMemory).");

        // Assert: one leaked factory or adapter per call would add 10,000+ references and many megabytes.
        Assert.True(handlesAfter - handlesBefore <= HandleTolerance, $"Handle count grew from {handlesBefore} to {handlesAfter}.");
        Assert.True(privateAfter - privateBefore <= PrivateBytesTolerance, $"Private bytes grew from {privateBefore:N0} to {privateAfter:N0}.");
    }

    [Fact]
    public void DirectMLDeviceId_HardwareAdapter_RunsOnnxSessionOnThatAdapter()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows only.");
        var adapter = PrimaryHardwareAdapter();
        Assert.SkipWhen(adapter is null, "No hardware GPU.");
        var path = OnnxTestModels.WriteTemp(_folder, "x2", OnnxTestModels.NearestUpscale(2));
        var before = _provider.QueryVideoMemory(adapter.Luid);
        Assert.NotNull(before);

        // Act: the scale probe already runs a 16×16 inference on the device; run one more tile while the session is alive.
        using var runner = OnnxModelRunner.Create(path, new OnnxRunnerOptions { UseDirectML = true, DeviceId = adapter.DirectMLDeviceId });
        var output = runner.Run(new PlanarImage(3, 64, 64));
        var during = _provider.QueryVideoMemory(adapter.Luid);

        // Assert: the session works, and this process's usage of exactly this adapter's local memory grew, so the device id maps to the LUID.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{adapter.Name} (DirectML device {adapter.DirectMLDeviceId}): process usage {before.CurrentUsage:N0} -> {during?.CurrentUsage:N0} bytes.");
        Assert.Equal((true, 2, 128, 128), (runner.UsesDirectML, runner.Scale, output.Width, output.Height));
        Assert.NotNull(during);
        Assert.True(during.CurrentUsage > before.CurrentUsage, $"Usage of {adapter.Name} did not grow ({before.CurrentUsage} -> {during.CurrentUsage}).");
    }

    private static (int Handles, long PrivateBytes) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return (process.HandleCount, process.PrivateMemorySize64);
    }

    private static void Report(IReadOnlyList<GpuAdapterInfo> adapters)
    {
        var output = TestContext.Current.TestOutputHelper;
        if (output is null)
        {
            return;
        }

        foreach (var adapter in adapters)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[{adapter.DirectMLDeviceId}] {adapter.Name} | LUID 0x{adapter.Luid:X16} | VEN 0x{adapter.VendorId:X4} DEV 0x{adapter.DeviceId:X4} | dedicated {adapter.DedicatedVideoMemory / (1024 * 1024)} MiB | shared {adapter.SharedSystemMemory / (1024 * 1024)} MiB | integrated {adapter.IsIntegrated} | software {adapter.IsSoftware} | driver {adapter.DriverVersion ?? "n/a"}"));
        }
    }

    private GpuAdapterInfo? PrimaryHardwareAdapter() =>
        _provider.GetAdapters().Where(adapter => !adapter.IsSoftware).OrderByDescending(adapter => adapter.DedicatedVideoMemory).FirstOrDefault();
}
