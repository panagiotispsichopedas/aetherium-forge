using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using AetheriumForge.App.Pages.Models;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Tests.Gpu;
using AetheriumForge.Core.Tests.Models;

namespace AetheriumForge.Core.Tests.App.Models;

/// <summary>Answers the Models page dialogs and records what they were shown.</summary>
internal sealed class FakeModelDialogs : IModelDialogs
{
    public bool ConfirmDownload { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

    public ModelFirstRunChoice FirstRunChoice { get; set; } = ModelFirstRunChoice.Later;

    public bool AcceptCalibration { get; set; }

    public string? PickedFile { get; set; }

    public List<IReadOnlyList<ModelDownloadItem>> DownloadConfirmations { get; } = [];

    public List<string> DeleteConfirmations { get; } = [];

    public List<ModelFirstRunRecommendation> FirstRunPrompts { get; } = [];

    public List<string> CalibrationOffers { get; } = [];

    public Task<bool> ConfirmDownloadAsync(IReadOnlyList<ModelDownloadItem> items)
    {
        DownloadConfirmations.Add(items);
        return Task.FromResult(ConfirmDownload);
    }

    public Task<bool> ConfirmDeleteAsync(string displayName)
    {
        DeleteConfirmations.Add(displayName);
        return Task.FromResult(ConfirmDelete);
    }

    public Task<ModelFirstRunChoice> ShowFirstRunAsync(ModelFirstRunRecommendation recommendation)
    {
        FirstRunPrompts.Add(recommendation);
        return Task.FromResult(FirstRunChoice);
    }

    public Task<bool> OfferCalibrationAsync(string family)
    {
        CalibrationOffers.Add(family);
        return Task.FromResult(AcceptCalibration);
    }

    public Task<string?> PickModelFileAsync() => Task.FromResult(PickedFile);
}

/// <summary>An <see cref="IForgeRunner"/> whose state the test sets.</summary>
internal sealed class FakeForgeRunner : IForgeRunner
{
    public event EventHandler<PipelineProgress>? ProgressChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    public PipelineState State { get; private set; } = PipelineState.Idle;

    public PipelineProgress? LastProgress => null;

    public void SetState(PipelineState state)
    {
        State = state;
        RunStateChanged?.Invoke(this, null);
    }

    public Task<PipelineRunSummary> StartAsync(ForgeRunRequest request) => throw new NotSupportedException();

    public void Pause()
    {
    }

    public void Unpause()
    {
    }

    public void RequestStop()
    {
    }
}

/// <summary>An adapter provider with scripted adapters and video memory.</summary>
internal sealed class FakeAdapterProvider(params GpuAdapterInfo[] adapters) : IGpuAdapterProvider
{
    public VideoMemoryInfo? Memory { get; set; }

    public static GpuAdapterInfo Discrete(ulong dedicatedGiB, string driver = "32.0.15.8266") =>
        new(0x42, 0, $"Test GPU {dedicatedGiB}GB", 0x10DE, 0x1B80, dedicatedGiB << 30, 16UL << 30, false, false, driver);

    public static GpuAdapterInfo Integrated() => new(0x43, 1, "Test iGPU", 0x8086, 0x3E92, 128UL << 20, 8UL << 30, true, false, "31.0.101.2111");

    public IReadOnlyList<GpuAdapterInfo> GetAdapters() => adapters;

    public VideoMemoryInfo? QueryVideoMemory(long luid) => Memory;
}

/// <summary>
/// A Models page over a temporary models folder and a fake GitHub release with three models: the two families the profiler recommends.
/// </summary>
internal sealed class ModelsPageFixture : IDisposable
{
    public const string X4Plus = GpuProfiler.StandardTierModelFamily;
    public const string X4PlusFp16 = GpuProfiler.StandardTierModelFamily + "-fp16";
    public const string General = GpuProfiler.LowTierModelFamily;
    public const int ChunkSize = 4096;

    private readonly HttpClient _http;

    public ModelsPageFixture(IGpuAdapterProvider? adapters = null, GpuProfiler? profiler = null)
    {
        Handler = new FakeHttpHandler(Respond);
        _http = new HttpClient(Handler);
        Options = new ModelSourceOptions { Owner = "o", Repo = "r", ModelsFolder = Folder.ModelsFolder };
        Adapters = adapters ?? new FakeAdapterProvider(FakeAdapterProvider.Discrete(8));
        Profiler = profiler ?? new GpuProfiler(Adapters);
        Store = new GpuProfileStore(Path.Combine(Folder.Root, "gpu-profiles.json"));
        Downloads = new ModelDownloadCoordinator(CreateLibrary);
        ViewModel = new ModelsViewModel(CreateLibrary, Downloads, Adapters, Profiler, Store, Dialogs, null, Runner, Ui.Post);
    }

    public TempModelsFolder Folder { get; } = new();

    /// <summary>Gets the page's UI thread: event handlers are posted to it, and tests run view-model calls on it.</summary>
    public TestUiThread Ui { get; } = new();

    public Dictionary<string, byte[]> Data { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [X4Plus] = ModelTestData.RandomBytes(96 * 1024, 1),
        [X4PlusFp16] = ModelTestData.RandomBytes(64 * 1024, 2),
        [General] = ModelTestData.RandomBytes(48 * 1024, 3),
    };

    public bool Online { get; set; } = true;

    public Action<string, DownloadRequest>? OnModelRequest { get; set; }

    public FakeHttpHandler Handler { get; }

    public ModelSourceOptions Options { get; }

    public FakeModelDialogs Dialogs { get; } = new();

    public FakeForgeRunner Runner { get; } = new();

    public IGpuAdapterProvider Adapters { get; }

    public GpuProfiler Profiler { get; }

    public GpuProfileStore Store { get; }

    public ModelDownloadCoordinator Downloads { get; }

    public ModelsViewModel ViewModel { get; }

    public IEnumerable<HttpRequestMessage> ModelRequests => Handler.Requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal));

    public ModelLibrary CreateLibrary() =>
        new(Folder.ModelsFolder, new ModelManifestClient(_http, Options), new ModelDownloader(_http, Folder.ModelsFolder, Options));

    public ModelRowViewModel Row(string name) => ViewModel.Models.Single(row => row.Name == name);

    public string ModelPath(string name) => Path.Combine(Folder.ModelsFolder, name + ".onnx");

    /// <summary>Writes a model file (and optionally a sidecar) as if it was installed earlier.</summary>
    public void Install(string name, byte[]? data = null, string precision = "fp32")
    {
        File.WriteAllBytes(ModelPath(name), data ?? Data[name]);
        File.WriteAllText(Path.ChangeExtension(ModelPath(name), ".json"), $$"""{ "displayName": "{{name}} installed", "scale": 4, "precision": "{{precision}}" }""");
    }

    public void Dispose()
    {
        Downloads.Dispose();
        Ui.Dispose();
        Store.Dispose();
        _http.Dispose();
        Folder.Dispose();
    }

    private static string Entry(string name, byte[] data, string precision, string tier) => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "name": "{{name}}", "family": "{{GpuProfiler.GetModelFamily(name)}}", "displayName": "{{name}} display", "fileName": "{{name}}.onnx",
          "url": "https://github.com/o/r/releases/download/models-v1/{{name}}.onnx",
          "size": {{data.Length}}, "sha256": "{{ModelTestData.Sha256(data)}}", "sidecarFileName": "{{name}}.json", "scale": 4,
          "precision": "{{precision}}", "tier": "{{tier}}", "licence": "BSD-3-Clause", "licenceFile": "LICENSE.txt",
          "licenceUrl": "https://github.com/o/r/releases/download/models-v1/LICENSE.txt", "recommendedFor": ["diffuse"]
        }
        """);

    private string ManifestJson()
    {
        var builder = new StringBuilder("""{ "schemaVersion": 1, "release": "models-v1", "models": [""");
        builder.Append(Entry(X4Plus, Data[X4Plus], "fp32", "standard")).Append(',');
        builder.Append(Entry(X4PlusFp16, Data[X4PlusFp16], "fp16", "standard")).Append(',');
        builder.Append(Entry(General, Data[General], "fp32", "low"));
        builder.Append("] }");
        return builder.ToString();
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (!Online)
        {
            throw new HttpRequestException("offline");
        }

        var path = request.RequestUri!.AbsolutePath;
        if (path == "/repos/o/r/releases")
        {
            return ModelTestData.Json("""[{ "tag_name": "models-v1", "draft": false, "assets": [] }]""");
        }

        if (path == "/o/r/releases/download/models-v1/models.json")
        {
            return ModelTestData.Json(ManifestJson());
        }

        if (path == "/o/r/releases/download/models-v1/LICENSE.txt")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelTestData.LicenceText) };
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".onnx", StringComparison.Ordinal) && Data.TryGetValue(name, out var data))
        {
            var download = new DownloadRequest(request);
            OnModelRequest?.Invoke(name, download);
            return ModelTestData.Serve(request, data, chunkSize: ChunkSize, onStream: download.Attach);
        }

        return ModelTestData.Status(HttpStatusCode.NotFound);
    }
}

/// <summary>One model request the fake server answered.</summary>
/// <param name="Request">The request.</param>
internal sealed record DownloadRequest(HttpRequestMessage Request)
{
    public ChunkedStream? Body { get; private set; }

    public void Attach(ChunkedStream body) => Body = body;
}

/// <summary>A GPU section over the simulated GPU from the profiler tests, so calibration really runs.</summary>
internal static class SimulatedModelsPage
{
    public static ModelsPageFixture Create(out SimulatedGpu gpu)
    {
        gpu = new SimulatedGpu();
        return new ModelsPageFixture(gpu, gpu.CreateProfiler());
    }
}

/// <summary>
/// A single UI thread with a <see cref="SynchronizationContext"/>, like Avalonia's dispatcher: posted actions and the continuations of
/// awaits started on it run there in order.
/// </summary>
internal sealed class TestUiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add((_ => action(), null));
        }
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread and completes when it does.</summary>
    public Task RunAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => _ = CompleteAsync(work, completion));
        return completion.Task;
    }

    /// <summary>Reads a value on the UI thread after everything posted before it.</summary>
    public Task<T> ReadAsync<T>(Func<T> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => completion.SetResult(read()));
        return completion.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private static async Task CompleteAsync(Func<Task> work, TaskCompletionSource completion)
    {
        try
        {
            await work();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    private sealed class Context(TestUiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!owner._queue.IsAddingCompleted)
            {
                owner._queue.Add((d, state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;
    }
}
