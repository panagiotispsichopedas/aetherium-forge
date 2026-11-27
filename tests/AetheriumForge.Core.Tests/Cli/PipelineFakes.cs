using AetheriumForge.Cli.Pipeline;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.Cli;

/// <summary>Source that returns a few dummy bytes for any path.</summary>
internal sealed class FakeTextureSource : ITextureSource
{
    public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 1, 2, 3 });
}

/// <summary>Codec that decodes to a 4×4 opaque image and "encodes" by writing a marker file at the output path.</summary>
internal sealed class FakeCodec : ITextureCodec
{
    private int _decodes;

    public Func<Exception?> DecodeFailure { get; set; } = () => null;

    public int Decodes => Volatile.Read(ref _decodes);

    public Task<RgbaImage> DecodeAsync(string ddsPath, string workFolder, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _decodes);
        if (DecodeFailure() is { } failure)
        {
            throw failure;
        }

        var pixels = new byte[4 * 4 * 4];
        Array.Fill(pixels, (byte)255);
        return Task.FromResult(new RgbaImage(4, 4, pixels));
    }

    public async Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, bool srgb, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputDdsPath)!);
        await File.WriteAllBytesAsync(outputDdsPath, [0x44, 0x44, 0x53, 0x20], ct);
    }
}

/// <summary>Upscaler (×4) whose behaviour per call is controlled by the test.</summary>
internal sealed class FakeUpscaler : IUpscaler
{
    private int _calls;

    /// <summary>Called with the 1-based call number before the result is produced.</summary>
    public Func<int, PauseToken, CancellationToken, Task> OnCall { get; set; } = (_, _, _) => Task.CompletedTask;

    public int Calls => Volatile.Read(ref _calls);

    public string ModelName => "fake";

    public int Scale => 4;

    public async Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        progress?.Report(new TileProgress(0, 1));
        await OnCall(call, pause, cancellationToken);
        progress?.Report(new TileProgress(1, 1));
        return new PlanarImage(rgb.Channels, rgb.Width * 4, rgb.Height * 4);
    }

    public void Dispose()
    {
    }
}

/// <summary>A temporary project (job store, work and output folders) with a <see cref="SequentialPipeline"/> over fakes.</summary>
internal sealed class PipelineFixture : IAsyncDisposable
{
    private PipelineFixture(string root, JobStore store)
    {
        Root = root;
        Store = store;
    }

    public string Root { get; }

    public JobStore Store { get; }

    public FakeCodec Codec { get; } = new();

    public FakeUpscaler Upscaler { get; } = new();

    public string WorkRoot => Path.Combine(Root, "work");

    public string OutputRoot => Path.Combine(Root, "output");

    public static async Task<PipelineFixture> CreateAsync(int queued, JobState state = JobState.Queued)
    {
        var root = Path.Combine(Path.GetTempPath(), $"af-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = await JobStore.OpenAsync(Path.Combine(root, "forge.db"));
        var jobs = Enumerable.Range(0, queued).Select(i => Job($@"textures\test\t{i:00}.dds", state));
        await store.UpsertJobsAsync(jobs);
        return new PipelineFixture(root, store);
    }

    public static JobRecord Job(string path, JobState state) =>
        new(0, path, "Skyrim - Textures0.bsa", 4, 4, "BC1_UNORM", "Diffuse", "test", 16, "fake", state, 0, null, null, null);

    public SequentialPipeline CreatePipeline(int? maxJobs = null, int maxAttempts = 2, Func<long, double, string?, Task>? markDone = null)
    {
        var processor = new TextureProcessor(new FakeTextureSource(), Codec, _ => Upscaler, OutputRoot, WorkRoot);
        var options = new PipelineOptions { WorkRoot = WorkRoot, OutputRoot = OutputRoot, MaxAttempts = maxAttempts, ProgressInterval = TimeSpan.Zero };
        return new SequentialPipeline(Store, processor, new JobMapper(ForgeRules.Default), options, maxJobs, null, null, markDone);
    }

    public async Task<Dictionary<JobState, int>> CountsAsync() => new(await Store.CountByStateAsync());

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Synchronous progress sink that keeps every snapshot.</summary>
internal sealed class RecordingProgress : IProgress<PipelineProgress>
{
    private readonly Lock _gate = new();
    private readonly List<PipelineProgress> _snapshots = [];

    public IReadOnlyList<PipelineProgress> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshots];
            }
        }
    }

    public void Report(PipelineProgress value)
    {
        lock (_gate)
        {
            _snapshots.Add(value);
        }
    }
}
