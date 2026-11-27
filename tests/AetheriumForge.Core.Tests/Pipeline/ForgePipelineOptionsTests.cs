using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline;

/// <summary>
/// <see cref="PipelineOptions.MaxJobs"/>, <see cref="PipelineOptions.ModelOverride"/> and the no-model ETA seed.
/// </summary>
public sealed class ForgePipelineOptionsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"af-pipeopt-{Guid.NewGuid():N}");
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkRoot => Path.Combine(_root, "work");

    private string OutputRoot => Path.Combine(_root, "out");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = await JobStore.OpenAsync(Path.Combine(_root, "forge.db"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MaxJobs_StartsOnlyThatManyAndLeavesRestQueued()
    {
        // Arrange
        await SeedAsync(10);
        var pipeline = Create(new PipelineFakeUpscaler(), new FakeEtaEstimator(), Options() with { MaxJobs = 3 });

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        var counts = await _store.CountByStateAsync(Ct);
        Assert.Equal((3, 0, 7, false), (summary.Done, summary.Requeued, counts[JobState.Queued], summary.Stopped));
    }

    [Fact]
    public async Task RunAsync_ModelOverride_UpscalesAndRecordsEtaWithOverride()
    {
        // Arrange
        await SeedAsync(2);
        var requested = new List<string>();
        var eta = new FakeEtaEstimator();
        var upscaler = new PipelineFakeUpscaler();
        var processor = new TextureProcessor(new PipelineFakeSource(), new PipelineFakeCodec(), model =>
        {
            lock (requested)
            {
                requested.Add(model);
            }

            return upscaler;
        }, OutputRoot, WorkRoot);
        var pipeline = new ForgePipeline(_store, processor, eta, Options() with { ModelOverride = "override-model" });

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal(["override-model"], requested.Concat(eta.Records.Select(r => r.Model)).Distinct().ToArray());
    }

    [Fact]
    public void Constructor_MaxJobsBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new PipelineFakeUpscaler(), new FakeEtaEstimator(), Options() with { MaxJobs = 0 }));
    }

    private PipelineOptions Options() => new()
    {
        WorkRoot = WorkRoot,
        OutputRoot = OutputRoot,
        DecodeWorkers = 2,
        PostProcessWorkers = 2,
        EncodeWorkers = 2,
        ProgressInterval = TimeSpan.FromMilliseconds(20),
    };

    private ForgePipeline Create(PipelineFakeUpscaler upscaler, FakeEtaEstimator eta, PipelineOptions options)
    {
        var processor = new TextureProcessor(new PipelineFakeSource(), new PipelineFakeCodec(), _ => upscaler, OutputRoot, WorkRoot);
        return new ForgePipeline(_store, processor, eta, options);
    }

    private Task<int> SeedAsync(int count) =>
        _store.UpsertJobsAsync(Enumerable.Range(0, count).Select(i => new JobRecord(
            0, $@"textures\t\opt_{i:D3}.dds", "Skyrim - Textures0.bsa", 8, 8, "DXT1", "Diffuse", "Large", 32, "job-model", JobState.Queued, 0, null, null, null)), Ct);
}
