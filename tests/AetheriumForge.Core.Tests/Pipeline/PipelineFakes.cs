using System.Collections.Concurrent;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline;

/// <summary>Tracks how many callers are inside a section at once.</summary>
internal sealed class ConcurrencyGauge
{
    private int _current;
    private int _max;

    public int Max => Volatile.Read(ref _max);

    public void Enter()
    {
        var current = Interlocked.Increment(ref _current);
        int max;
        do
        {
            max = Volatile.Read(ref _max);
        }
        while (current > max && Interlocked.CompareExchange(ref _max, current, max) != max);
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}

/// <summary>Returns three bytes for every texture; throws <see cref="IOException"/> for paths containing <see cref="FailMarker"/>.</summary>
internal sealed class PipelineFakeSource : ITextureSource
{
    public const string FailMarker = "broken";

    public ConcurrentDictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default)
    {
        Reads.AddOrUpdate(path, 1, (_, n) => n + 1);
        return path.Contains(FailMarker, StringComparison.OrdinalIgnoreCase)
            ? Task.FromException<byte[]>(new IOException($"cannot read {path}"))
            : Task.FromResult(new byte[] { 1, 2, 3 });
    }
}

/// <summary>Decodes to a solid 8×8 image and "encodes" by writing a temp file in the work folder and moving it into place.</summary>
internal sealed class PipelineFakeCodec : ITextureCodec
{
    public static readonly byte[] EncodedBytes = "DDS fake"u8.ToArray();

    public TimeSpan DecodeDelay { get; init; }

    public TimeSpan EncodeDelay { get; init; }

    public ConcurrencyGauge DecodeGauge { get; } = new();

    public ConcurrencyGauge EncodeGauge { get; } = new();

    public ConcurrentDictionary<string, int> Commits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int CommitCount => Commits.Values.Sum();

    public async Task<RgbaImage> DecodeAsync(string ddsPath, string workFolder, CancellationToken ct = default)
    {
        DecodeGauge.Enter();
        try
        {
            await Task.Delay(DecodeDelay, ct);
            var image = new RgbaImage(8, 8);
            Array.Fill(image.Pixels, (byte)200);
            return image;
        }
        finally
        {
            DecodeGauge.Exit();
        }
    }

    /// <summary>Gets a value indicating whether every encode throws (a codec that always fails).</summary>
    public bool FailAllEncodes { get; init; }

    /// <summary>Gets a value indicating whether a cancelled encode throws <see cref="IOException"/> (like a killed texconv) instead of OCE.</summary>
    public bool ThrowIOExceptionWhenCancelled { get; init; }

    /// <summary>Gets an action run after each encode wrote its file (receives the path the codec wrote).</summary>
    public Action<string>? OnEncoded { get; init; }

    /// <summary>Gets the paths the codec was asked to write.</summary>
    public ConcurrentQueue<string> EncodeTargets { get; } = new();

    public async Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, bool srgb, CancellationToken ct = default)
    {
        EncodeGauge.Enter();
        try
        {
            EncodeTargets.Enqueue(outputDdsPath);
            if (FailAllEncodes)
            {
                throw new IOException("encoder failed");
            }

            var temp = Path.Combine(workFolder, "encode.tmp");
            await File.WriteAllBytesAsync(temp, EncodedBytes, ct);
            try
            {
                await Task.Delay(EncodeDelay, ct);
            }
            catch (OperationCanceledException) when (ThrowIOExceptionWhenCancelled)
            {
                throw new IOException("texconv was killed");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputDdsPath)!);
            File.Move(temp, outputDdsPath, overwrite: true);
            Commits.AddOrUpdate(outputDdsPath, 1, (_, n) => n + 1);
            OnEncoded?.Invoke(outputDdsPath);
        }
        finally
        {
            EncodeGauge.Exit();
        }
    }
}

/// <summary>Nearest-neighbour ×4 upscaler with a controllable number of tiles and delay per tile.</summary>
internal sealed class PipelineFakeUpscaler : IUpscaler
{
    private int _tilesRun;

    public int Tiles { get; init; } = 2;

    public TimeSpan TileDelay { get; init; }

    public ConcurrencyGauge Gauge { get; } = new();

    public int TilesRun => Volatile.Read(ref _tilesRun);

    public string ModelName => "fake";

    public int Scale => 4;

    public async Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        Gauge.Enter();
        try
        {
            for (var tile = 0; tile < Tiles; tile++)
            {
                await pause.WaitWhilePausedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TileDelay, cancellationToken);
                Interlocked.Increment(ref _tilesRun);
                progress?.Report(new TileProgress(tile + 1, Tiles));
            }

            var output = new PlanarImage(rgb.Channels, rgb.Width * Scale, rgb.Height * Scale);
            for (var c = 0; c < rgb.Channels; c++)
            {
                for (var y = 0; y < output.Height; y++)
                {
                    for (var x = 0; x < output.Width; x++)
                    {
                        output.Data[output.IndexOf(c, x, y)] = rgb.Data[rgb.IndexOf(c, x / Scale, y / Scale)];
                    }
                }
            }

            return output;
        }
        finally
        {
            Gauge.Exit();
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Records every finished job; estimates one second per remaining megapixel.</summary>
internal sealed class FakeEtaEstimator : IEtaEstimator
{
    private int _maxEntries;

    public ConcurrentQueue<(string Model, double Megapixels, TimeSpan Elapsed)> Records { get; } = new();

    /// <summary>Gets the most entries any <see cref="Estimate"/> call received.</summary>
    public int MaxEntries => Volatile.Read(ref _maxEntries);

    public void Seed(string model, double secondsPerMegapixel)
    {
    }

    public void Record(string model, double sourceMegapixels, TimeSpan elapsed) => Records.Enqueue((model, sourceMegapixels, elapsed));

    public TimeSpan? Estimate(IEnumerable<(string Model, double SourceMegapixels)> remaining, double parallelism = 1.0)
    {
        var entries = remaining.ToArray();
        int max;
        do
        {
            max = Volatile.Read(ref _maxEntries);
        }
        while (entries.Length > max && Interlocked.CompareExchange(ref _maxEntries, entries.Length, max) != max);

        return TimeSpan.FromSeconds(entries.Sum(r => r.SourceMegapixels) / parallelism);
    }
}

/// <summary>Thread-safe progress sink that keeps every report.</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly ConcurrentQueue<(DateTime At, T Value)> _reports = new();

    public IReadOnlyList<(DateTime At, T Value)> Reports => [.. _reports];

    public void Report(T value) => _reports.Enqueue((DateTime.UtcNow, value));
}
