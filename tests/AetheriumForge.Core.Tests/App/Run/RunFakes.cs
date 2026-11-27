using AetheriumForge.App.Pages.Run;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.App.Run;

internal sealed class FakeProjectSession : IProjectSession
{
    public event EventHandler? Changed;

    public event EventHandler? JobsChanged;

    public ProjectInfo? Current { get; set; }

    public JobStore? Store { get; set; }

    public int JobsChangedCount { get; private set; }

    public Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void NotifyJobsChanged()
    {
        JobsChangedCount++;
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeAdapterProvider(params GpuAdapterInfo[] adapters) : IGpuAdapterProvider
{
    public VideoMemoryInfo? Memory { get; set; }

    public IReadOnlyList<GpuAdapterInfo> GetAdapters() => adapters;

    public VideoMemoryInfo? QueryVideoMemory(long luid) => Memory;
}

internal sealed class FakePipeline : IForgePipeline
{
    private readonly TaskCompletionSource<PipelineRunSummary> _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PipelineState State { get; private set; }

    public int Pauses { get; private set; }

    public int Unpauses { get; private set; }

    public CancellationToken StopToken { get; private set; }

    public PipelineProgress? ReportOnStart { get; set; }

    public void Finish(PipelineRunSummary summary) => _finish.TrySetResult(summary);

    public async Task<PipelineRunSummary> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken stopToken)
    {
        State = PipelineState.Running;
        StopToken = stopToken;
        if (ReportOnStart is { } snapshot)
        {
            progress?.Report(snapshot);
        }

        Started.TrySetResult();
        using var registration = stopToken.Register(() => _finish.TrySetResult(new PipelineRunSummary(1, 0, 2, true, TimeSpan.FromSeconds(3))));
        var summary = await _finish.Task.ConfigureAwait(false);
        State = PipelineState.Idle;
        return summary;
    }

    public void Pause()
    {
        Pauses++;
        State = PipelineState.Paused;
    }

    public void Unpause()
    {
        Unpauses++;
        State = PipelineState.Running;
    }
}

internal sealed class FakeBackend : IForgeRunBackend
{
    public List<ModelInfo> Installed { get; } = [];

    public Dictionary<string, GpuProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakePipeline Pipeline { get; set; } = new();

    public ForgeRunPlan? LastPlan { get; private set; }

    public int ResourcesDisposed { get; private set; }

    public string? TexconvError { get; set; }

    public string ModelsFolder => @"C:\models";

    public IReadOnlyList<ModelInfo> DiscoverModels() => Installed;

    public string FindTexconv() => TexconvError is null ? @"C:\tools\texconv.exe" : throw new FileNotFoundException(TexconvError);

    public Task<GpuProfile?> FindProfileAsync(GpuAdapterInfo adapter, string family, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.TryGetValue(family, out var profile) ? profile : null);

    public IForgeRunResources CreateRun(ForgeRunPlan plan)
    {
        LastPlan = plan;
        return new Resources(this, Pipeline);
    }

    private sealed class Resources(FakeBackend owner, IForgePipeline pipeline) : IForgeRunResources
    {
        public IForgePipeline Pipeline => pipeline;

        public void Dispose() => owner.ResourcesDisposed++;
    }
}

internal sealed class InlineDispatcher : IRunUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeRunner : IForgeRunner
{
    public event EventHandler<PipelineProgress>? ProgressChanged;

    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    public PipelineState State { get; set; }

    public PipelineProgress? LastProgress { get; set; }

    public ForgeRunRequest? LastRequest { get; private set; }

    public int Pauses { get; private set; }

    public int Unpauses { get; private set; }

    public int Stops { get; private set; }

    public TaskCompletionSource<PipelineRunSummary> Run { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<PipelineRunSummary> StartAsync(ForgeRunRequest request)
    {
        LastRequest = request;
        return Run.Task;
    }

    public void Pause()
    {
        Pauses++;
        State = PipelineState.Paused;
    }

    public void Unpause()
    {
        Unpauses++;
        State = PipelineState.Running;
    }

    public void RequestStop()
    {
        Stops++;
        State = PipelineState.Stopping;
    }

    public void SetState(PipelineState state, PipelineRunSummary? summary = null)
    {
        State = state;
        RunStateChanged?.Invoke(this, summary);
    }

    public void Report(PipelineProgress progress) => ProgressChanged?.Invoke(this, progress);
}

internal static class RunTestData
{
    public static GpuAdapterInfo Gtx1080 { get; } = new(Luid: 42, DirectMLDeviceId: 1, "NVIDIA GeForce GTX 1080", 0x10DE, 0x1B80, 8UL << 30, 16UL << 30, false, false, "32.0.15.6094");

    public static GpuAdapterInfo Basic { get; } = new(Luid: 7, DirectMLDeviceId: 2, "Microsoft Basic Render Driver", 0x1414, 0x8C, 0, 4UL << 30, false, true, null);

    public static ModelInfo Model(string name, string precision) => new(name, $@"C:\models\{name}.onnx", name, 4, precision, null, []);

    public static GpuProfile Profile(string family, string precision, int tile, double secondsPerMegapixel, int staleDeviceId = 0) =>
        new(Gtx1080.Luid + 1000, Gtx1080.Name, Gtx1080.DriverVersion, staleDeviceId, family, precision, tile, secondsPerMegapixel, GpuTier.Standard, family, DateTimeOffset.UnixEpoch)
        {
            VendorId = Gtx1080.VendorId,
            DeviceId = Gtx1080.DeviceId,
        };

    public static JobRecord Job(string path, string? model, string kind = "Diffuse") =>
        new(0, path, "Skyrim - Textures0.bsa", 512, 512, "BC1_UNORM", kind, "Large", 2048, model, JobState.Queued, 0, null, null, null);

    public static PipelineProgress Progress(PipelineState state = PipelineState.Running, int done = 3, int failed = 1, int remaining = 6) =>
        new(state, done, failed, remaining, done + failed + remaining,
            [
                new StageActivity(PipelineStage.Decode, @"textures\a\b.dds", 1, 2, 0, 0),
                new StageActivity(PipelineStage.Upscale, @"textures\c\d.dds", 1, 0, 12, 48),
                new StageActivity(PipelineStage.PostProcess, null, 0, 0, 0, 0),
                new StageActivity(PipelineStage.Encode, null, 0, 1, 0, 0),
            ],
            1.25, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(3725), "boom");
}
