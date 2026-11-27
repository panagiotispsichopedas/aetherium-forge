using AetheriumForge.Core.Archives;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Pipeline;

/// <summary>
/// Runs the real pipeline on three vanilla textures: stop after the first commit, run again, verify every output.
/// Opt in with <c>AETHERIUM_SKYRIM_DATA</c> (game Data folder, read only), <c>AETHERIUM_TEXCONV</c> (texconv.exe) and
/// <c>AETHERIUM_TEST_MODEL</c> (an .onnx model); <c>AETHERIUM_TEST_DIRECTML=1</c> runs the model on the GPU.
/// </summary>
public sealed class ForgePipelineIntegrationTests : IDisposable
{
    private const string Archive = "Skyrim - Textures0.bsa";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", $"pipeline-real-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RealTexturesStoppedThenResumed_AllDoneAndOutputsDecode()
    {
        // Arrange
        var data = Environment.GetEnvironmentVariable("AETHERIUM_SKYRIM_DATA");
        var texconvPath = Environment.GetEnvironmentVariable("AETHERIUM_TEXCONV");
        var modelPath = Environment.GetEnvironmentVariable("AETHERIUM_TEST_MODEL");
        Assert.SkipUnless(
            Directory.Exists(data) && File.Exists(texconvPath) && File.Exists(modelPath),
            "Set AETHERIUM_SKYRIM_DATA, AETHERIUM_TEXCONV and AETHERIUM_TEST_MODEL to run the real pipeline test.");

        var work = Path.Combine(_root, "work");
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_root);
        var jobs = PickTextures(Path.Combine(data!, Archive), 3);

        var store = await JobStore.OpenAsync(Path.Combine(_root, "project.db"), Ct);
        await using var storeScope = store;
        await store.UpsertJobsAsync(jobs, Ct);

        var model = new ModelInfo("test-model", modelPath!, "test model", null, ModelCatalog.DefaultPrecision, null, []);
        var runner = new OnnxRunnerOptions { UseDirectML = Environment.GetEnvironmentVariable("AETHERIUM_TEST_DIRECTML") == "1" };
        using var upscalers = new UpscalerFactory([model], runner, new TilingOptions());
        using var source = new ArchiveTextureSource(data!);
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = texconvPath! });
        var options = new PipelineOptions { WorkRoot = work, OutputRoot = output, DecodeWorkers = 1, PostProcessWorkers = 1, EncodeWorkers = 1 };
        ForgePipeline NewPipeline() => new(store, new TextureProcessor(source, texconv, upscalers.Get, output, work), new FakeEtaEstimator(), options);

        // Act: first run stops as soon as one texture is committed; the second run finishes the rest.
        using var stop = new CancellationTokenSource();
        var first = await NewPipeline().RunAsync(new StopAfterFirstCommit(stop), stop.Token);
        var afterStop = await StatesAsync(store);
        var second = await NewPipeline().RunAsync(null, Ct);

        // Assert
        var finalStates = await StatesAsync(store);
        var outputs = new List<(int Width, int Height, int Target)>();
        foreach (var job in jobs)
        {
            var file = Path.Combine(output, job.Path);
            var decoded = await texconv.DecodeAsync(file, Path.Combine(_root, "verify"), Ct);
            outputs.Add((decoded.Width, decoded.Height, job.Target));
        }

        TestContext.Current.SendDiagnosticMessage(
            $"first run: done {first.Done}, requeued {first.Requeued}, stopped {first.Stopped}, {first.Elapsed.TotalSeconds:F1} s; states after stop: {string.Join(", ", afterStop)}; "
            + $"second run: done {second.Done}, {second.Elapsed.TotalSeconds:F1} s; outputs: {string.Join(", ", outputs.Select(o => $"{o.Width}×{o.Height}"))}");
        Assert.Equal(
            (true, 0, 3, true, 0),
            (first.Stopped, afterStop.Count(s => s == JobState.Running), finalStates.Count(s => s == JobState.Done), outputs.All(o => Math.Max(o.Width, o.Height) == o.Target), Directory.GetFileSystemEntries(work).Length));
    }

    private static List<JobRecord> PickTextures(string archivePath, int count)
    {
        using var reader = BsaReader.Open(archivePath);
        var header = new byte[DdsHeader.MinimumLength];
        var picked = new List<JobRecord>();
        foreach (var entry in reader.Entries.Where(e => e.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
        {
            if (TextureClassifier.GetKind(entry.Path) != TextureKind.Diffuse || reader.ReadPrefix(entry, header) < DdsHeader.MinimumLength
                || !DdsHeader.TryParse(header, out var dds) || dds.IsCubemap || dds.MaxDimension != 256)
            {
                continue;
            }

            picked.Add(new JobRecord(0, entry.Path, Path.GetFileName(archivePath), dds.Width, dds.Height, dds.FormatName, nameof(TextureKind.Diffuse), null, 512, "test-model", JobState.Queued, 0, null, null, null));
            if (picked.Count == count)
            {
                return picked;
            }
        }

        throw new InvalidOperationException($"Found only {picked.Count} suitable textures in {archivePath}.");
    }

    private static async Task<List<JobState>> StatesAsync(JobStore store)
    {
        var states = new List<JobState>();
        await foreach (var job in store.GetJobsAsync(null, Ct))
        {
            states.Add(job.State);
        }

        return states;
    }

    private sealed class StopAfterFirstCommit(CancellationTokenSource stop) : IProgress<PipelineProgress>
    {
        public void Report(PipelineProgress value)
        {
            if (value.Done >= 1 && !stop.IsCancellationRequested)
            {
                stop.Cancel();
            }
        }
    }
}
