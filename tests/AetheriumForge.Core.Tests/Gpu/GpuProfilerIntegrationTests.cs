using System.Globalization;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Tests.Gpu;

/// <summary>
/// Calibrates a real model on the real GPU. Opt in with <c>AETHERIUM_TEST_MODEL</c> (an FP32 <c>.onnx</c>), optionally
/// <c>AETHERIUM_TEST_MODEL_FP16</c> (its FP16 variant), <c>AETHERIUM_TEST_DIRECTML=1</c> and <c>AETHERIUM_TEST_DIRECTML_DEVICE</c>.
/// <c>AETHERIUM_TEST_OUTPUT_DIR</c> saves the profile (<c>gpu-profiles.json</c>) and the calibration log (<c>calibration.log</c>) there.
/// The adapter provider is a stub that reports 8 GB and reads no real memory, so only out-of-memory errors and the time budget limit the tile.
/// </summary>
public sealed class GpuProfilerIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CalibrateAsync_RealModelFromEnvironment_ProducesProfile()
    {
        var fp32Path = Environment.GetEnvironmentVariable("AETHERIUM_TEST_MODEL");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(fp32Path) && File.Exists(fp32Path), "Set AETHERIUM_TEST_MODEL to a real FP32 .onnx model to run this test.");

        // Arrange
        var useDirectML = Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML") == "1";
        var deviceId = int.TryParse(Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML_DEVICE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var device) ? device : 0;
        var variants = new List<ModelInfo> { Model(fp32Path!, "fp32") };
        var fp16Path = Environment.GetEnvironmentVariable("AETHERIUM_TEST_MODEL_FP16");
        if (!string.IsNullOrWhiteSpace(fp16Path) && File.Exists(fp16Path))
        {
            variants.Add(Model(fp16Path, "fp16"));
        }

        var adapter = new GpuAdapterInfo(1, deviceId, "Integration test GPU", 0, 0, 8UL << 30, 0, false, false, "test");
        var logger = new ListLogger();
        var profiler = new GpuProfiler(new StubAdapterProvider(adapter), logger: logger);
        var options = new CalibrationOptions { UseDirectML = useDirectML, TimeBudget = TimeSpan.FromSeconds(useDirectML ? 30 : 10) };
        var progress = new ListProgress();

        // Act
        var profile = await profiler.CalibrateAsync(adapter, variants, options, progress, Ct);

        var outputDirectory = Environment.GetEnvironmentVariable("AETHERIUM_TEST_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            using var store = new GpuProfileStore(Path.Combine(outputDirectory, "gpu-profiles.json"));
            await store.SaveAsync(profile, Ct);
            await File.AppendAllLinesAsync(Path.Combine(outputDirectory, "calibration.log"), logger.Lines, Ct);
        }

        // Assert
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{profile.Model}: precision {profile.Precision}, tile {profile.TileSize}, {profile.SecondsPerMegapixel:F2} s/MP " +
            $"({progress.Reports.Count} progress reports, DirectML {useDirectML}, device {deviceId})");
        Assert.True(profile.TileSize is >= 64 and <= 1024 && profile.SecondsPerMegapixel > 0, $"tile {profile.TileSize}, {profile.SecondsPerMegapixel} s/MP");
    }

    private static ModelInfo Model(string path, string precision)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return new ModelInfo(name, Path.GetFullPath(path), name, null, precision, null, []);
    }

    private sealed class ListLogger : ILogger<GpuProfiler>
    {
        private readonly Lock _gate = new();

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Lines.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    private sealed class StubAdapterProvider(GpuAdapterInfo adapter) : IGpuAdapterProvider
    {
        public IReadOnlyList<GpuAdapterInfo> GetAdapters() => [adapter];

        public VideoMemoryInfo? QueryVideoMemory(long luid) => null;
    }
}
