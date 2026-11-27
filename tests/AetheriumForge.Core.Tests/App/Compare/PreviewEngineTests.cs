using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class PreviewEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"af-preview-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PreviewRequest Request(string name, string? model = "fake", TextureKind kind = TextureKind.Diffuse) =>
        new($"textures\\clutter\\{name}.dds", "Skyrim - Textures0.bsa", kind, 16, 16, model);

    private static PreviewEngine Engine(CompareFakeBackend backend, Func<PipelineState>? state = null, long cap = PreviewMemoryCache.DefaultCapacityBytes, Func<LookInput, LookSettings, RgbaImage>? renderer = null) =>
        new(backend, state ?? (() => PipelineState.Idle), cap, renderer ?? LookRenderer.Render, null);

    [Fact]
    public async Task GetSampleAsync_SameRequestTwice_UpscalesOnce()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = Engine(backend);

        // Act
        await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);
        await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.Equal(1, backend.Upscaler.Calls);
    }

    [Fact]
    public async Task GetSampleAsync_Built_ResizesAiAndLanczosToTarget()
    {
        // Arrange
        using var engine = Engine(new CompareFakeBackend());

        // Act
        var result = await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.Equal((16, 16, 16, 16, 8), (result.Sample!.AiRgb.Width, result.Sample.AiRgb.Height, result.Sample.LanczosRgb.Width, result.Sample.LanczosRgb.Height, result.Sample.Source.Width));
    }

    [Fact]
    public async Task GetSampleAsync_DifferentModel_BuildsSeparateSample()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = Engine(backend);
        await engine.GetSampleAsync(Request("a", "fake"), null, CancellationToken.None);

        // Act
        await engine.GetSampleAsync(Request("a", "other"), null, CancellationToken.None);

        // Assert
        Assert.Equal(2, engine.BuiltSamples);
    }

    [Fact]
    public async Task GetSampleAsync_OverMemoryCap_EvictsLeastRecentlyUsed()
    {
        // Arrange: room for two 16×16 samples, not three.
        var backend = new CompareFakeBackend();
        var probe = Engine(backend);
        var size = (await probe.GetSampleAsync(Request("probe"), null, CancellationToken.None)).Sample!.SizeInBytes;
        probe.Dispose();
        using var engine = Engine(backend, cap: (size * 2) + 1);
        await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);
        await engine.GetSampleAsync(Request("b"), null, CancellationToken.None);
        await engine.GetSampleAsync(Request("a"), null, CancellationToken.None); // a is now the most recent

        // Act
        await engine.GetSampleAsync(Request("c"), null, CancellationToken.None);

        // Assert
        Assert.Equal((true, false, true), (engine.IsCached(Request("a")), engine.IsCached(Request("b")), engine.IsCached(Request("c"))));
    }

    [Fact]
    public async Task GetSampleAsync_RunnerNotIdle_ReturnsGpuBusyWithoutUpscaling()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = Engine(backend, () => PipelineState.Running);

        // Act
        var result = await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.Equal((false, PreviewEngine.GpuBusyReason, 0), (result.IsAvailable, result.UnavailableReason, backend.Upscaler.Calls));
    }

    [Fact]
    public async Task GetSampleAsync_RunnerPausedButSampleCached_ReturnsCachedSample()
    {
        // Arrange
        var state = PipelineState.Idle;
        using var engine = Engine(new CompareFakeBackend(), () => state);
        await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);
        state = PipelineState.Paused;

        // Act
        var result = await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void IsUpscalingAvailable_RunnerStopping_IsFalse()
    {
        // Arrange
        using var engine = Engine(new CompareFakeBackend(), () => PipelineState.Stopping);

        // Act
        var available = engine.IsUpscalingAvailable;

        // Assert
        Assert.False(available);
    }

    [Fact]
    public async Task GetSampleAsync_NoProject_ReturnsNoProjectReason()
    {
        // Arrange
        using var engine = Engine(new CompareFakeBackend { HasProject = false });

        // Act
        var result = await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.Equal(PreviewEngine.NoProjectReason, result.UnavailableReason);
    }

    [Fact]
    public async Task GetSampleAsync_ParallaxMap_SkipsTheModel()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = Engine(backend);

        // Act
        var result = await engine.GetSampleAsync(Request("a_p", kind: TextureKind.Parallax), null, CancellationToken.None);

        // Assert
        Assert.Equal((false, 0), (result.Sample!.UsedModel, backend.Upscaler.Calls));
    }

    [Fact]
    public async Task GetSampleAsync_DiskCacheFromEarlierSession_DoesNotUpscaleAgain()
    {
        // Arrange
        var first = new CompareFakeBackend { DiskCacheFolder = _root };
        using (var engine = Engine(first))
        {
            await engine.GetSampleAsync(Request("a"), null, CancellationToken.None);
        }

        var second = new CompareFakeBackend { DiskCacheFolder = _root };
        using var restarted = Engine(second);

        // Act
        var result = await restarted.GetSampleAsync(Request("a"), null, CancellationToken.None);

        // Assert
        Assert.Equal((true, 0), (result.IsAvailable, second.Upscaler.Calls));
    }

    [Fact]
    public async Task SubmitRenderAsync_NewerRequestsWhileRendering_DropsStaleAndSkipsIntermediate()
    {
        // Arrange: the first render blocks until released; two more requests arrive meanwhile.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new List<double>();
        RgbaImage Renderer(LookInput input, LookSettings look)
        {
            lock (rendered)
            {
                rendered.Add(look.Brightness);
            }

            if (look.Brightness == 0.1)
            {
                started.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
            }

            return LookRenderer.Render(input, look);
        }

        using var engine = Engine(new CompareFakeBackend(), renderer: Renderer);
        var sample = (await engine.GetSampleAsync(Request("a"), null, CancellationToken.None)).Sample!;
        var stale = engine.SubmitRenderAsync(sample, new LookSettings { Brightness = 0.1 }, draft: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var skipped = engine.SubmitRenderAsync(sample, new LookSettings { Brightness = 0.2 }, draft: false);
        var latest = engine.SubmitRenderAsync(sample, new LookSettings { Brightness = 0.3 }, draft: false);

        // Act
        release.SetResult();
        var outcomes = await Task.WhenAll(stale, skipped, latest).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((false, false, true, 0.3, 2), (outcomes[0].IsCurrent, outcomes[1].IsCurrent, outcomes[2].IsCurrent, outcomes[2].Result!.Look.Brightness, rendered.Count));
    }

    [Fact]
    public async Task SubmitRenderAsync_Draft_RendersDownscaledImage()
    {
        // Arrange
        using var engine = Engine(new CompareFakeBackend());
        engine.DraftMaxSide = 8;
        var sample = (await engine.GetSampleAsync(Request("a"), null, CancellationToken.None)).Sample!;

        // Act
        var outcome = await engine.SubmitRenderAsync(sample, LookSettings.Neutral, draft: true);

        // Assert
        Assert.Equal((8, true), (outcome.Result!.Image.Width, outcome.Result.IsDraft));
    }

    [Fact]
    public async Task SubmitRenderAsync_Full_RendersTargetSize()
    {
        // Arrange
        using var engine = Engine(new CompareFakeBackend());
        var sample = (await engine.GetSampleAsync(Request("a"), null, CancellationToken.None)).Sample!;

        // Act
        var outcome = await engine.SubmitRenderAsync(sample, LookSettings.Neutral, draft: false);

        // Assert
        Assert.Equal((16, 16, false), (outcome.Result!.Image.Width, outcome.Result.Image.Height, outcome.Result.IsDraft));
    }

    [Fact]
    public void ScaleForDraft_HalfSize_HalvesSharpenRadius()
    {
        // Arrange
        var look = new LookSettings { Sharpen = new SharpenSettings(0.5, 2.0) };

        // Act
        var scaled = PreviewEngine.ScaleForDraft(look, 0.5);

        // Assert
        Assert.Equal(1.0, scaled.Sharpen.Radius, 6);
    }

    [Fact]
    public async Task GetSourceAsync_SameTextureTwice_ReadsOnce()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = Engine(backend);

        // Act
        await engine.GetSourceAsync("textures\\a.dds", "x.bsa", CancellationToken.None);
        await engine.GetSourceAsync("textures\\a.dds", "x.bsa", CancellationToken.None);

        // Assert
        Assert.Equal(1, backend.FakeSource.Reads);
    }
}
