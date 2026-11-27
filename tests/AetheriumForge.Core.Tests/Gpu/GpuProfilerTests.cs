using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace AetheriumForge.Core.Tests.Gpu;

public sealed class GpuProfilerTests
{
    private const ulong GiB = 1UL << 30;
    private const int Overlap = 24;

    private static readonly ModelInfo _fp32 = SimulatedGpu.Model("realesrgan-x4plus", "fp32");
    private static readonly ModelInfo _fp16 = SimulatedGpu.Model("realesrgan-x4plus-fp16", "fp16");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CalibrationOptions Options(int maxTileProbes = 16) => new() { MaxTileProbes = maxTileProbes, TimeBudget = TimeSpan.FromHours(1) };

    private static GpuAdapterInfo Adapter(string name, ulong dedicated, bool integrated = false, bool software = false, int deviceId = 0) =>
        new(deviceId + 100, deviceId, name, 0x10DE, 0x1B80, dedicated, 8 * GiB, integrated, software, "1.0");

    /// <summary>
    /// The tile the memory rule should choose: each probe runner is warmed up at the smallest tile, the baseline is read, and the peak
    /// above that baseline must stay within 80 % of the available memory at that baseline.
    /// </summary>
    private static int ExpectedMemoryTile(SimulatedGpu gpu, ulong extraBaseUsage = 0)
    {
        var warmup = gpu.MemoryCurve(64 + (2 * Overlap));
        var available = gpu.Budget - (gpu.BaseUsage + extraBaseUsage + gpu.WeightBytes + warmup);
        var allowance = (ulong)(available * 0.8);
        return LargestFittingTile(tile =>
        {
            var needed = gpu.MemoryCurve(tile + (2 * Overlap));
            return needed <= warmup || needed - warmup <= allowance;
        });
    }

    /// <summary>Largest multiple of 64 in [64, 1024] that satisfies <paramref name="fits"/>.</summary>
    private static int LargestFittingTile(Func<int, bool> fits) =>
        Enumerable.Range(1, 16).Select(step => step * 64).Where(fits).DefaultIfEmpty(0).Max();

    [Fact]
    public void SelectAdapter_DiscreteIntegratedAndSoftware_PicksDiscreteWithMostMemory()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();
        GpuAdapterInfo[] adapters =
        [
            Adapter("Microsoft Basic Render Driver", 32 * GiB, software: true, deviceId: 3),
            Adapter("Intel UHD", 16 * GiB, integrated: true, deviceId: 2),
            Adapter("GTX 1060", 6 * GiB, deviceId: 1),
            Adapter("GTX 1080", 8 * GiB, deviceId: 0),
        ];

        // Act
        var selected = profiler.SelectAdapter(adapters);

        // Assert
        Assert.Equal("GTX 1080", selected?.Name);
    }

    [Fact]
    public void SelectAdapter_OnlyIntegratedAndSoftware_FallsBackToIntegrated()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();
        GpuAdapterInfo[] adapters = [Adapter("Microsoft Basic Render Driver", 0, software: true), Adapter("Intel UHD", 128UL << 20, integrated: true, deviceId: 1)];

        // Act
        var selected = profiler.SelectAdapter(adapters);

        // Assert
        Assert.Equal("Intel UHD", selected?.Name);
    }

    [Fact]
    public void SelectAdapter_OnlySoftware_ReturnsNull()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var selected = profiler.SelectAdapter([Adapter("Microsoft Basic Render Driver", 0, software: true)]);

        // Assert
        Assert.Null(selected);
    }

    [Fact]
    public void SelectAdapter_NoArguments_UsesProvider()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var selected = profiler.SelectAdapter();

        // Assert
        Assert.Equal(SimulatedGpu.Adapter, selected);
    }

    [Theory]
    [InlineData(8UL << 30, true, GpuTier.Low)]
    [InlineData(2UL << 30, false, GpuTier.Low)]
    [InlineData((4UL << 30) - 1, false, GpuTier.Low)]
    [InlineData(4UL << 30, false, GpuTier.Standard)]
    [InlineData(8UL << 30, false, GpuTier.Standard)]
    public void ClassifyTier_MemoryAndKind_ReturnsTier(ulong dedicated, bool integrated, GpuTier expected)
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var tier = profiler.ClassifyTier(Adapter("GPU", dedicated, integrated));

        // Assert
        Assert.Equal(expected, tier);
    }

    [Theory]
    [InlineData(GpuTier.Low, "realesr-general-x4v3")]
    [InlineData(GpuTier.Standard, "realesrgan-x4plus")]
    public void RecommendModelFamily_Tier_ReturnsFamily(GpuTier tier, string expected)
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var family = profiler.RecommendModelFamily(tier);

        // Assert
        Assert.Equal(expected, family);
    }

    [Theory]
    [InlineData("realesrgan-x4plus", "realesrgan-x4plus")]
    [InlineData("realesrgan-x4plus-fp16", "realesrgan-x4plus")]
    [InlineData("realesr-general-x4v3_FP32", "realesr-general-x4v3")]
    [InlineData("fp16", "fp16")]
    public void GetModelFamily_Name_StripsPrecisionSuffix(string name, string expected)
    {
        // Act
        var family = GpuProfiler.GetModelFamily(name);

        // Assert
        Assert.Equal(expected, family);
    }

    [Theory]
    [InlineData(null, 512, 64, 1024, 256)]
    [InlineData(null, 64, 64, 1024, null)]
    [InlineData(null, 100, 64, 1024, 64)]
    [InlineData(512, null, 64, 1024, 1024)]
    [InlineData(1024, null, 64, 1024, null)]
    [InlineData(600, null, 64, 1000, 1000)]
    [InlineData(512, 1024, 64, 1024, 768)]
    [InlineData(768, 832, 64, 1024, null)]
    [InlineData(768, 896, 64, 1024, 832)]
    public void NextTileSize_SearchState_ReturnsNextCandidate(int? fit, int? failure, int min, int max, int? expected)
    {
        // Act
        var next = GpuProfiler.NextTileSize(fit, failure, min, max);

        // Assert
        Assert.Equal(expected, next);
    }

    [Fact]
    public void EstimateTileSize_LargeBudget_ClampsToMaximum()
    {
        // Act
        var tile = GpuProfiler.EstimateTileSize(1UL << 50, new CalibrationOptions());

        // Assert
        Assert.Equal(1024, tile);
    }

    [Fact]
    public void EstimateTileSize_TinyBudget_ClampsToMinimum()
    {
        // Act
        var tile = GpuProfiler.EstimateTileSize(1024, new CalibrationOptions());

        // Assert
        Assert.Equal(64, tile);
    }

    [Fact]
    public async Task CalibrateAsync_Fp16Faster_ChoosesFp16()
    {
        // Arrange
        var gpu = new SimulatedGpu { Duration = (precision, side) => TimeSpan.FromTicks((precision == "fp16" ? 1L : 2L) * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.Equal(("fp16", "realesrgan-x4plus"), (profile.Precision, profile.Model));
    }

    [Fact]
    public async Task CalibrateAsync_Fp32Faster_ChoosesFp32()
    {
        // Arrange (Pascal: FP16 runs at a fraction of the FP32 rate)
        var gpu = new SimulatedGpu { Duration = (precision, side) => TimeSpan.FromTicks((precision == "fp16" ? 3L : 1L) * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp16, _fp32], Options(), null, Ct);

        // Assert
        Assert.Equal("fp32", profile.Precision);
    }

    [Fact]
    public async Task CalibrateAsync_Fp16WithinMargin_KeepsFp32()
    {
        // Arrange (FP16 only 2 % faster; the default margin is 5 %)
        var gpu = new SimulatedGpu { Duration = (precision, side) => TimeSpan.FromTicks((precision == "fp16" ? 98L : 100L) * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.Equal("fp32", profile.Precision);
    }

    [Fact]
    public async Task CalibrateAsync_BothPrecisions_BenchmarksAtSameTile()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var options = Options() with { WarmupRuns = 2, TimedRuns = 3 };
        var profiler = gpu.CreateProfiler();

        // Act
        await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], options, null, Ct);

        // Assert (first 5 runs FP32, next 5 FP16, all at the same input size)
        var benchmark = gpu.Runs.Take(10).ToList();
        Assert.Equal(
            (5, 5, 1),
            (benchmark.Count(run => run.Precision == "fp32"), benchmark.Count(run => run.Precision == "fp16"), benchmark.Select(run => run.Side).Distinct().Count()));
    }

    [Fact]
    public async Task CalibrateAsync_OnlyFp32_RunsNoFp16()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(("fp32", false), (profile.Precision, gpu.Runs.Any(run => run.Precision == "fp16")));
    }

    [Fact]
    public async Task CalibrateAsync_Fp16FailsToLoad_KeepsFp32AsProvisional()
    {
        // Arrange
        var gpu = new SimulatedGpu
        {
            Duration = (precision, side) => TimeSpan.FromTicks((precision == "fp16" ? 1L : 4L) * side * side),
            CreateFailure = (_, model) => model.Precision == "fp16" ? SimulatedGpu.CreateOnnxRuntimeException("[ErrorCode:NotImplemented] Could not find an implementation for Conv(11) node") : null,
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.Equal(("fp32", true), (profile.Precision, profile.IsProvisional));
    }

    [Fact]
    public async Task CalibrateAsync_Fp16RunFails_Propagates()
    {
        // Arrange
        var gpu = new SimulatedGpu
        {
            Failure = (precision, _) => precision == "fp16" ? SimulatedGpu.CreateOnnxRuntimeException("[ErrorCode:Fail] Non-zero status code returned while running Conv node") : null,
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct));

        // Assert
        Assert.IsType<OnnxRuntimeException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_Fp16LoadReportsDeviceRemoved_Propagates()
    {
        // Arrange
        var gpu = new SimulatedGpu
        {
            CreateFailure = (_, model) => model.Precision == "fp16" ? SimulatedGpu.CreateOnnxRuntimeException("[ErrorCode:Fail] 887A0005 The GPU device instance has been suspended") : null,
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct));

        // Assert
        Assert.IsType<OnnxRuntimeException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_BothVariantsLoad_ProfileIsNotProvisional()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.False(profile.IsProvisional);
    }

    [Fact]
    public async Task CalibrateAsync_RunnerFactoryOutOfMemory_TreatsTileAsFailedProbe()
    {
        // Arrange: creation 1 is the reference runner; creation 2 is the first probe runner (tile 512), which fails to load.
        var gpu = new SimulatedGpu { CreateFailure = (creation, _) => creation == 2 ? SimulatedGpu.OutOfMemoryError() : null };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(448, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_FreshProbeRunner_WarmsUpAtMinimumTileFirst()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var profiler = gpu.CreateProfiler();

        // Act
        await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert (runs 1-5: reference at 304; run 6: warm-up at 64 + 2 x 24; run 7: the first probe at 512 + 2 x 24)
        Assert.Equal((112, 560), (gpu.Runs[5].Side, gpu.Runs[6].Side));
    }

    [Fact]
    public async Task CalibrateAsync_SlowerBelowReferenceTile_KeepsSmallTile()
    {
        // Arrange: memory limits the tile to 128 (input 176), where runs are 3 times slower per pixel than the 256 reference.
        var gpu = new SimulatedGpu
        {
            MemoryCurve = side => (ulong)side * (ulong)side * 131072,
            Duration = (_, side) => TimeSpan.FromTicks((side < 250 ? 3L : 1L) * side * side),
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal((128, 128), (profile.TileSize, ExpectedMemoryTile(gpu)));
    }

    [Fact]
    public async Task CalibrateAsync_SlowdownRejectsMinimumTile_ExceptionSaysTooSlow()
    {
        // Arrange: probe runners run 10 times slower than the reference runner at the same (minimum) tile.
        var gpu = new SimulatedGpu { DurationScale = index => index == 0 ? 1 : 10 };
        var options = Options() with { MaxTileSize = 64, PrecisionTileSize = 64 };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], options, null, Ct));

        // Assert
        Assert.Contains("slower per pixel", (exception as VideoMemoryExhaustedException)?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalibrateAsync_UsageGrowsBeforeTileSearch_UsesFreshBaseline()
    {
        // Arrange: another process takes 2 GiB during the last reference run (run 5), before the first probe runner loads.
        var gpu = new SimulatedGpu();
        gpu.BeforeRun = run =>
        {
            if (run == 5)
            {
                gpu.BaseUsage += 2UL << 30;
            }
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert (768 with the stale baseline, 640 with the fresh one)
        Assert.Equal((640, 640), (profile.TileSize, ExpectedMemoryTile(gpu)));
    }

    [Fact]
    public async Task CalibrateAsync_TransientPeakDuringRun_IsSampled()
    {
        // Arrange: inputs above 600 briefly need 10 GiB during the run and free it before returning.
        var gpu = new SimulatedGpu { MemoryCurve = _ => 1024, TransientBytes = side => side > 600 ? 10UL << 30 : 0 };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(512, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_ConcurrentCallsForOneAdapter_RunOneAtATime()
    {
        // Arrange
        var gpu = new SimulatedGpu { BeforeRun = _ => Thread.Sleep(1) };
        var profiler = gpu.CreateProfiler();

        // Act
        await Task.WhenAll(
            profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct),
            profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct));

        // Assert
        Assert.Equal(1, gpu.MaxConcurrentRuns);
    }

    [Fact]
    public void ResolveAdapter_SameHardwareNewLuid_ReturnsLowestDeviceIndex()
    {
        // Arrange
        var profile = Profile(SimulatedGpu.Adapter with { Luid = 7, DirectMLDeviceId = 3 });
        GpuAdapterInfo[] adapters =
        [
            SimulatedGpu.Adapter with { Luid = 9, DirectMLDeviceId = 2 },
            SimulatedGpu.Adapter with { Luid = 8, DirectMLDeviceId = 1 },
            SimulatedGpu.Adapter with { Luid = 10, DirectMLDeviceId = 0, Name = "Other GPU" },
        ];

        // Act
        var resolved = GpuProfiler.ResolveAdapter(profile, adapters);

        // Assert
        Assert.Equal(8L, resolved?.Luid);
    }

    [Fact]
    public void ResolveAdapter_MatchingLuid_ReturnsThatAdapter()
    {
        // Arrange
        var profile = Profile(SimulatedGpu.Adapter with { Luid = 9 });
        GpuAdapterInfo[] adapters = [SimulatedGpu.Adapter with { Luid = 8, DirectMLDeviceId = 0 }, SimulatedGpu.Adapter with { Luid = 9, DirectMLDeviceId = 1 }];

        // Act
        var resolved = GpuProfiler.ResolveAdapter(profile, adapters);

        // Assert
        Assert.Equal(1, resolved?.DirectMLDeviceId);
    }

    [Fact]
    public void ResolveAdapter_HardwareGone_ReturnsNull()
    {
        // Arrange
        var profile = Profile(SimulatedGpu.Adapter);

        // Act
        var resolved = GpuProfiler.ResolveAdapter(profile, [SimulatedGpu.Adapter with { DeviceId = 0x2786, Name = "RTX 4070" }]);

        // Assert
        Assert.Null(resolved);
    }

    private static GpuProfile Profile(GpuAdapterInfo adapter) =>
        new(adapter.Luid, adapter.Name, adapter.DriverVersion, adapter.DirectMLDeviceId, "realesrgan-x4plus", "fp32", 512, 9, GpuTier.Standard, "realesrgan-x4plus", ManualTimeProvider.Start)
        {
            VendorId = adapter.VendorId,
            DeviceId = adapter.DeviceId,
        };


    [Fact]
    public async Task CalibrateAsync_MemoryCurve_ConvergesToLargestTileUnderBudget()
    {
        // Arrange: 8 GiB budget, 1 GiB already used, 64 MiB weights, 8 KiB per input pixel.
        var gpu = new SimulatedGpu();
        var expected = ExpectedMemoryTile(gpu);
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(expected, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_SteepMemoryCurve_HalvesBelowEstimate()
    {
        // Arrange: 64 KiB per input pixel, four times the profiler's first estimate.
        var gpu = new SimulatedGpu { MemoryCurve = side => (ulong)side * (ulong)side * 65536 };
        var expected = ExpectedMemoryTile(gpu);
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal((expected, true), (profile.TileSize, gpu.Runs.Any(run => run.Side > expected + (2 * Overlap))));
    }

    [Fact]
    public async Task CalibrateAsync_RejectedTile_DisposesRunnerBeforeSmallerProbe()
    {
        // Arrange
        var gpu = new SimulatedGpu { MemoryCurve = side => (ulong)side * (ulong)side * 65536 };
        var profiler = gpu.CreateProfiler();

        // Act
        await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert (every runner is disposed and the simulated usage is back to zero)
        Assert.Equal((true, 0UL), (gpu.Runners.All(runner => runner.Disposed), gpu.CurrentUsage));
    }

    [Fact]
    public async Task CalibrateAsync_SharedMemorySpillAboveSize_RejectsSlowTiles()
    {
        // Arrange: memory never runs out, but above a 500 input the driver pages to shared memory and runs four times slower per pixel.
        var gpu = new SimulatedGpu
        {
            ReportsMemory = false,
            Duration = (_, side) => TimeSpan.FromTicks((side > 500 ? 4L : 1L) * side * side),
        };
        var expected = LargestFittingTile(tile => tile + (2 * Overlap) <= 500);
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(expected, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_SlowdownWithinLimit_KeepsLargerTile()
    {
        // Arrange: large inputs run 1.5 times slower per pixel, under the default limit of 2.
        var gpu = new SimulatedGpu { MemoryCurve = _ => 1024, Duration = (_, side) => TimeSpan.FromTicks((side > 500 ? 3L : 2L) * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(1024, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_RunnerRetainsBuffersOfEverySize_ProbesEachTileOnFreshRunner()
    {
        // Arrange: the GTX 1080 / realesrgan-x4plus curve measured through DXGI. DirectML keeps the buffers of every input size a session
        // has run (MiB per size: 240 at 112, 1474 up to 304, 3010 up to 496, 6147 at 560), and a first run at a new size is slower the more
        // the session already holds. Reusing the 256 probe's runner for 384 made that run 2.1x slower than the reference, so 384 was
        // rejected and calibration settled on 320 (or 256); on a fresh runner every size up to 448 fits the 80 % allowance.
        const ulong MiB = 1UL << 20;
        var gpu = new SimulatedGpu
        {
            Budget = 7291 * MiB,
            BaseUsage = 0,
            WeightBytes = 79 * MiB,
            RetainsEveryInputSize = true,
            MemoryCurve = side => side switch
            {
                <= 112 => 240 * MiB,
                <= 304 => 1474 * MiB,
                <= 496 => 3010 * MiB,
                _ => 6147 * MiB,
            },
            Duration = (_, side) => TimeSpan.FromTicks(54L * side * side),
            FirstRunAtNewSizeScale = held => 1.3 + ((double)held / GiB / 2),
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(448, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_SlowFirstRunAtNewSize_ConfirmsWithWarmRun()
    {
        // Arrange: every first run at a new size is 3 times slower (shader compilation), later runs are steady.
        var gpu = new SimulatedGpu { FirstRunAtNewSizeScale = _ => 3 };
        var expected = ExpectedMemoryTile(gpu);
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(expected, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_SmallMemoryCurve_ClampsToMaxTile()
    {
        // Arrange
        var gpu = new SimulatedGpu { MemoryCurve = _ => 1024 };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal((1024, 1024 + (2 * Overlap)), (profile.TileSize, gpu.Runs.Max(run => run.Side)));
    }

    [Fact]
    public async Task CalibrateAsync_CustomBounds_StaysWithinBounds()
    {
        // Arrange
        var gpu = new SimulatedGpu { MemoryCurve = _ => 1024 };
        var options = Options() with { MinTileSize = 128, MaxTileSize = 448, PrecisionTileSize = 128 };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], options, null, Ct);

        // Assert
        Assert.Equal((448, true), (profile.TileSize, gpu.Runs.All(run => run.Side is >= 128 + (2 * Overlap) and <= 448 + (2 * Overlap))));
    }

    [Fact]
    public async Task CalibrateAsync_OutOfMemoryAboveSize_ConvergesBelowLimit()
    {
        // Arrange: no memory reporting, so only out-of-memory errors limit the tile.
        var gpu = new SimulatedGpu { ReportsMemory = false, OutOfMemoryAboveSide = 700 };
        var expected = LargestFittingTile(tile => tile + (2 * Overlap) <= 700);
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(expected, profile.TileSize);
    }

    [Fact]
    public async Task CalibrateAsync_OutOfMemoryInPrecisionBenchmark_RetriesSmallerTile()
    {
        // Arrange: the precision benchmark starts at 256 (input 304), which runs out of memory.
        var gpu = new SimulatedGpu { ReportsMemory = false, OutOfMemoryAboveSide = 200, Duration = (precision, side) => TimeSpan.FromTicks((precision == "fp16" ? 1L : 2L) * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.Equal(("fp16", 128), (profile.Precision, profile.TileSize));
    }

    [Fact]
    public async Task CalibrateAsync_OutOfMemoryAtMinimumTile_ThrowsVideoMemoryExhausted()
    {
        // Arrange
        var gpu = new SimulatedGpu { ReportsMemory = false, OutOfMemoryAboveSide = 32 };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct));

        // Assert
        Assert.IsType<VideoMemoryExhaustedException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_NonMemoryFailure_Propagates()
    {
        // Arrange
        var gpu = new SimulatedGpu { Failure = (_, _) => SimulatedGpu.CreateOnnxRuntimeException("[ErrorCode:Fail] 887A0005 device removed") };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct));

        // Assert
        Assert.IsType<OnnxRuntimeException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_DurationProportionalToInputArea_ComputesSecondsPerSourceMegapixel()
    {
        // Arrange: each run takes 1 µs per input pixel; throughput counts only the tile (source) pixels.
        var gpu = new SimulatedGpu { Duration = (_, side) => TimeSpan.FromTicks(10L * side * side) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, Ct);

        // Assert
        var input = profile.TileSize + (2.0 * Overlap);
        var expected = input * input / 1e6 / (profile.TileSize * (double)profile.TileSize / 1e6);
        Assert.Equal(expected, profile.SecondsPerMegapixel, 9);
    }

    [Fact]
    public async Task CalibrateAsync_SlowGpu_StopsGrowingWithinTimeBudget()
    {
        // Arrange: 10 µs per input pixel, so a 512 tile run (560²) takes about 3 s and a 1024 tile run (1072²) about 11.5 s.
        var gpu = new SimulatedGpu { MemoryCurve = _ => 1024, Duration = (_, side) => TimeSpan.FromTicks(100L * side * side) };
        var options = new CalibrationOptions { TimeBudget = TimeSpan.FromSeconds(30) };
        var profiler = gpu.CreateProfiler();

        // Act
        var profile = await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], options, null, Ct);

        // Assert
        Assert.True(profile.TileSize < 1024 && gpu.Time.GetUtcNow() - ManualTimeProvider.Start <= options.TimeBudget, $"tile {profile.TileSize}, {gpu.Time.GetUtcNow() - ManualTimeProvider.Start}");
    }

    [Fact]
    public async Task CalibrateAsync_Success_FillsProfile()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        gpu.Time.Advance(TimeSpan.FromMinutes(5));
        var profiler = gpu.CreateProfiler();
        var adapter = SimulatedGpu.Adapter;

        // Act
        var profile = await profiler.CalibrateAsync(adapter, [_fp32], Options(), null, Ct);

        // Assert
        Assert.Equal(
            (adapter.Luid, adapter.Name, adapter.DriverVersion, adapter.DirectMLDeviceId, adapter.VendorId, adapter.DeviceId, GpuTier.Standard, "realesrgan-x4plus", true),
            (profile.AdapterLuid, profile.AdapterName, profile.DriverVersion, profile.DirectMLDeviceId, profile.VendorId, profile.DeviceId, profile.Tier, profile.RecommendedModelFamily, profile.CalibratedAt > ManualTimeProvider.Start.AddMinutes(5)));
    }

    [Fact]
    public async Task CalibrateAsync_Adapter_PassesDeviceIdToRunners()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var adapter = SimulatedGpu.Adapter with { DirectMLDeviceId = 2 };
        var profiler = gpu.CreateProfiler();

        // Act
        await profiler.CalibrateAsync(adapter, [_fp32, _fp16], Options(), null, Ct);

        // Assert
        Assert.All(gpu.Created, created => Assert.Equal((2, true), (created.Options.DeviceId, created.Options.UseDirectML)));
    }

    [Fact]
    public async Task CalibrateAsync_Progress_ReportsEveryStep()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var progress = new ListProgress();
        var profiler = gpu.CreateProfiler();

        // Act
        await profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), progress, Ct);

        // Assert
        Assert.Equal(
            [GpuProfiler.PrecisionStep, GpuProfiler.TileStep, GpuProfiler.ThroughputStep],
            progress.Reports.Select(report => report.Step).Distinct());
    }

    [Fact]
    public async Task CalibrateAsync_CancelledDuringRun_ThrowsAndDisposesRunners()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gpu = new SimulatedGpu();
        gpu.BeforeRun = run =>
        {
            if (run == 12)
            {
                cancellation.Cancel();
            }
        };
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, _fp16], Options(), null, cancellation.Token));

        // Assert
        Assert.Equal((true, 12, true), (exception is OperationCanceledException, gpu.Runs.Count, gpu.Runners.All(runner => runner.Disposed)));
    }

    [Fact]
    public async Task CalibrateAsync_AlreadyCancelled_RunsNothing()
    {
        // Arrange
        var gpu = new SimulatedGpu();
        var profiler = gpu.CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], Options(), null, new CancellationToken(true)));

        // Assert
        Assert.Equal((true, 0), (exception is OperationCanceledException, gpu.Created.Count));
    }

    [Fact]
    public async Task CalibrateAsync_MixedFamilies_ThrowsArgumentException()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32, SimulatedGpu.Model("realesr-general-x4v3-fp16", "fp16")], Options(), null, Ct));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_NoVariants_ThrowsArgumentException()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [], Options(), null, Ct));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task CalibrateAsync_InvalidOptions_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var profiler = new SimulatedGpu().CreateProfiler();

        // Act
        var exception = await Record.ExceptionAsync(() => profiler.CalibrateAsync(SimulatedGpu.Adapter, [_fp32], new CalibrationOptions { BudgetFraction = 1.5 }, null, Ct));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }
}
