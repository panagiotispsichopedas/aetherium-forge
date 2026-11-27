using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Tests.App.Run;

public sealed class ForgeRunnerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "af-runner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProjectSession _session = new();
    private readonly FakeBackend _backend = new();
    private FakeAdapterProvider _adapters = new(RunTestData.Basic, RunTestData.Gtx1080);
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = await JobStore.OpenAsync(Path.Combine(_root, "forge.db"), Ct);
        _session.Store = _store;
        _session.Current = new ProjectInfo(_root, @"C:\Game", @"C:\Game\Data", Path.Combine(_root, "output"), Path.Combine(_root, "work"), ForgeRules.Default);
        _backend.Installed.Add(RunTestData.Model("realesrgan-x4plus", "fp32"));
        _backend.Installed.Add(RunTestData.Model("realesrgan-x4plus-fp16", "fp16"));
        _backend.Installed.Add(RunTestData.Model("realesr-general-x4v3", "fp32"));
        await _store.UpsertJobsAsync(
            [
                RunTestData.Job(@"textures\a.dds", "realesrgan-x4plus"),
                RunTestData.Job(@"textures\b.dds", "realesrgan-x4plus"),
                RunTestData.Job(@"textures\c_p.dds", null, "Parallax"),
            ],
            Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file is left for the OS to clean up.
        }
    }

    [Fact]
    public async Task StartAsync_NoProject_ThrowsInvalidOperation()
    {
        // Arrange
        _session.Current = null;
        using var runner = CreateRunner();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new ForgeRunRequest()));
    }

    [Fact]
    public async Task StartAsync_NoProject_ReturnsToIdle()
    {
        // Arrange
        _session.Current = null;
        using var runner = CreateRunner();

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new ForgeRunRequest()));

        // Assert
        Assert.Equal(PipelineState.Idle, runner.State);
    }

    [Fact]
    public async Task StartAsync_TexconvMissing_ThrowsWithMessage()
    {
        // Arrange
        _backend.TexconvError = "texconv.exe was not found. Tried: x";
        using var runner = CreateRunner();

        // Act
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => runner.StartAsync(new ForgeRunRequest()));

        // Assert
        Assert.Contains("texconv.exe was not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhileRunning_ThrowsInvalidOperation()
    {
        // Arrange
        using var runner = CreateRunner();
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);

        // Act
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new ForgeRunRequest()));

        // Assert
        Assert.Contains("already", second.Message, StringComparison.Ordinal);
        runner.RequestStop();
        await run.WaitAsync(Ct);
    }

    [Fact]
    public async Task StartAsync_PipelineStarted_StateIsRunning()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);

        // Assert
        Assert.Equal(PipelineState.Running, runner.State);
        _backend.Pipeline.Finish(new PipelineRunSummary(2, 0, 0, false, TimeSpan.FromSeconds(1)));
        await run.WaitAsync(Ct);
    }

    [Fact]
    public async Task StartAsync_RunEnds_RaisesStartAndEndEventsAndReturnsToIdle()
    {
        // Arrange
        using var runner = CreateRunner();
        var events = new List<PipelineRunSummary?>();
        var statesAtEvent = new List<PipelineState>();
        runner.RunStateChanged += (_, summary) =>
        {
            events.Add(summary);
            statesAtEvent.Add(runner.State);
        };
        var expected = new PipelineRunSummary(2, 0, 0, false, TimeSpan.FromSeconds(1));

        // Act
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);
        _backend.Pipeline.Finish(expected);
        var summary = await run.WaitAsync(Ct);

        // Assert
        Assert.Equal((IReadOnlyList<PipelineRunSummary?>)[null, expected], events);
        Assert.Equal(PipelineState.Idle, statesAtEvent[^1]);
        Assert.Equal(expected, summary);
        Assert.Equal(PipelineState.Idle, runner.State);
    }

    [Fact]
    public async Task StartAsync_RunEnds_NotifiesJobsChangedAndDisposesResources()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);
        _backend.Pipeline.Finish(new PipelineRunSummary(2, 0, 0, false, TimeSpan.Zero));
        await run.WaitAsync(Ct);

        // Assert
        Assert.Equal((1, 1), (_session.JobsChangedCount, _backend.ResourcesDisposed));
    }

    [Fact]
    public async Task StartAsync_PipelineReportsProgress_RaisesProgressChangedAndKeepsLastProgress()
    {
        // Arrange
        var snapshot = RunTestData.Progress();
        _backend.Pipeline.ReportOnStart = snapshot;
        using var runner = CreateRunner();
        var received = new List<PipelineProgress>();
        runner.ProgressChanged += (_, p) => received.Add(p);

        // Act
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);
        _backend.Pipeline.Finish(new PipelineRunSummary(3, 1, 0, false, TimeSpan.Zero));
        await run.WaitAsync(Ct);

        // Assert
        Assert.Equal((snapshot, snapshot), (received.Single(), runner.LastProgress));
    }

    [Fact]
    public async Task Pause_Running_PausesPipelineAndStateIsPaused()
    {
        // Arrange
        using var runner = CreateRunner();
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);

        // Act
        runner.Pause();

        // Assert
        Assert.Equal((PipelineState.Paused, 1), (runner.State, _backend.Pipeline.Pauses));
        runner.RequestStop();
        await run.WaitAsync(Ct);
    }

    [Fact]
    public async Task Unpause_Paused_ResumesPipelineAndStateIsRunning()
    {
        // Arrange
        using var runner = CreateRunner();
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);
        runner.Pause();

        // Act
        runner.Unpause();

        // Assert
        Assert.Equal((PipelineState.Running, 1), (runner.State, _backend.Pipeline.Unpauses));
        runner.RequestStop();
        await run.WaitAsync(Ct);
    }

    [Fact]
    public async Task RequestStop_Paused_CancelsStopTokenAndStateIsStopping()
    {
        // Arrange
        var pipeline = new FakePipeline();
        _backend.Pipeline = pipeline;
        using var runner = CreateRunner();
        var states = new List<PipelineState>();
        var run = runner.StartAsync(new ForgeRunRequest());
        await pipeline.Started.Task.WaitAsync(Ct);
        runner.Pause();

        // Act
        runner.RequestStop();
        states.Add(runner.State);
        var summary = await run.WaitAsync(Ct);
        states.Add(runner.State);

        // Assert
        Assert.Equal((true, true, PipelineState.Idle), (pipeline.StopToken.IsCancellationRequested, summary.Stopped, states[^1]));
    }

    [Fact]
    public void Pause_Idle_DoesNothing()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        runner.Pause();
        runner.RequestStop();

        // Assert
        Assert.Equal(PipelineState.Idle, runner.State);
    }

    [Fact]
    public async Task BuildPlanAsync_Fp16ProfileAndVariantInstalled_UsesFp16VariantTileAndSeed()
    {
        // Arrange
        _backend.Profiles["realesrgan-x4plus"] = RunTestData.Profile("realesrgan-x4plus", "fp16", 512, 0.75);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(), Ct);

        // Assert
        var model = Assert.Single(plan.Models.Values);
        Assert.Equal(("realesrgan-x4plus", "realesrgan-x4plus-fp16", 512, (double?)0.75), (model.Model, model.Variant, model.TileSize, model.SecondsPerMegapixel));
    }

    [Fact]
    public async Task BuildPlanAsync_ProfileWithStaleDeviceIndex_ReResolvesDevice()
    {
        // Arrange
        _backend.Profiles["realesrgan-x4plus"] = RunTestData.Profile("realesrgan-x4plus", "fp32", 384, 1.0, staleDeviceId: 5);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(), Ct);

        // Assert
        Assert.Equal(RunTestData.Gtx1080.DirectMLDeviceId, plan.Models["realesrgan-x4plus"].DeviceId);
    }

    [Fact]
    public async Task BuildPlanAsync_Fp16ProfileButOnlyFp32Installed_UsesFp32VariantDefaultTileAndNoSeed()
    {
        // Arrange
        _backend.Profiles["realesr-general-x4v3"] = RunTestData.Profile("realesr-general-x4v3", "fp16", 256, 0.5);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(ModelOverride: "realesr-general-x4v3"), Ct);

        // Assert
        var model = plan.Models["realesr-general-x4v3"];
        Assert.Equal(("realesr-general-x4v3", ForgeRunPlanning.DefaultTileSize, (double?)null), (model.Variant, model.TileSize, model.SecondsPerMegapixel));
    }

    [Fact]
    public async Task BuildPlanAsync_ExplicitFp16OverrideWithFp32Profile_RunsFp16WithDefaultTileAndNoSeed()
    {
        // Arrange
        _backend.Profiles["realesrgan-x4plus"] = RunTestData.Profile("realesrgan-x4plus", "fp32", 512, 2.0);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(ModelOverride: "realesrgan-x4plus-fp16"), Ct);

        // Assert
        var model = plan.Models["realesrgan-x4plus-fp16"];
        Assert.Equal(("realesrgan-x4plus-fp16", ForgeRunPlanning.DefaultTileSize, (double?)null), (model.Variant, model.TileSize, model.SecondsPerMegapixel));
    }

    [Fact]
    public async Task BuildPlanAsync_ExplicitFp16OverrideWithFp16Profile_UsesProfileTileAndSeed()
    {
        // Arrange
        _backend.Profiles["realesrgan-x4plus"] = RunTestData.Profile("realesrgan-x4plus", "fp16", 512, 0.8);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(ModelOverride: "realesrgan-x4plus-fp16"), Ct);

        // Assert
        var model = plan.Models["realesrgan-x4plus-fp16"];
        Assert.Equal((512, (double?)0.8), (model.TileSize, model.SecondsPerMegapixel));
    }

    [Fact]
    public async Task StartAsync_JobsChangedSubscriberThrows_StillRaisesRunEndAndReturnsSummary()
    {
        // Arrange
        _session.ThrowOnNotify = true;
        using var runner = CreateRunner();
        var ends = new List<PipelineRunSummary>();
        runner.RunStateChanged += (_, summary) =>
        {
            if (summary is not null)
            {
                ends.Add(summary);
            }
        };
        var expected = new PipelineRunSummary(2, 0, 0, false, TimeSpan.Zero);

        // Act
        var run = runner.StartAsync(new ForgeRunRequest());
        await _backend.Pipeline.Started.Task.WaitAsync(Ct);
        _backend.Pipeline.Finish(expected);
        var summary = await run.WaitAsync(Ct);

        // Assert
        Assert.Equal((expected, expected, PipelineState.Idle), (summary, Assert.Single(ends), runner.State));
    }

    [Fact]
    public void KillChildProcesses_TrackedProcess_KillsIt()
    {
        // Arrange
        var tracker = new ChildProcessTracker();
        using var runner = new ForgeRunner(_session, _adapters, _backend, tracker, NullLogger.Instance);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        using var registration = tracker.Track(process);

        // Act
        var killed = runner.KillChildProcesses();
        process.WaitForExit(10_000);

        // Assert
        Assert.Equal((1, true), (killed, process.HasExited));
    }

    [Fact]
    public void ChooseVariant_BaseNameWithoutProfile_PrefersBaseNameWhateverTheCatalogOrder()
    {
        // Arrange
        ModelInfo[] installed = [RunTestData.Model("m-x4-fp16", "fp16"), RunTestData.Model("m-x4", "fp32")];

        // Act
        var variant = ForgeRunPlanning.ChooseVariant("m-x4", null, installed);

        // Assert
        Assert.Equal(new ModelVariant("m-x4", "fp32"), variant);
    }

    [Fact]
    public void ChooseVariant_BaseNameNotInstalledWithoutProfile_FallsBackToFirstVariantByName()
    {
        // Arrange
        ModelInfo[] installed = [RunTestData.Model("m-x4-fp16", "fp16"), RunTestData.Model("m-x4-bf16", "bf16")];

        // Act
        var variant = ForgeRunPlanning.ChooseVariant("m-x4", null, installed);

        // Assert
        Assert.Equal(new ModelVariant("m-x4-fp16", "fp16"), variant);
    }

    [Fact]
    public void ChooseVariant_ExplicitFp16NameWithFp32Profile_HonoursTheName()
    {
        // Arrange
        ModelInfo[] installed = [RunTestData.Model("m-x4", "fp32"), RunTestData.Model("m-x4-fp16", "fp16")];

        // Act
        var variant = ForgeRunPlanning.ChooseVariant("m-x4-fp16", RunTestData.Profile("m-x4", "fp32", 384, 1.0), installed);

        // Assert
        Assert.Equal(new ModelVariant("m-x4-fp16", "fp16"), variant);
    }

    [Fact]
    public void ChooseVariant_ExplicitVariantNotInstalled_ReturnsNull()
    {
        // Act
        var variant = ForgeRunPlanning.ChooseVariant("m-x4-fp16", null, [RunTestData.Model("m-x4", "fp32")]);

        // Assert
        Assert.Null(variant);
    }

    [Fact]
    public async Task BuildPlanAsync_NoProfile_UsesSelectedAdapterDefaultTileAndNoSeed()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(), Ct);

        // Assert
        var model = plan.Models["realesrgan-x4plus"];
        Assert.Equal(("realesrgan-x4plus", RunTestData.Gtx1080.DirectMLDeviceId, ForgeRunPlanning.DefaultTileSize, (double?)null), (model.Variant, model.DeviceId, model.TileSize, model.SecondsPerMegapixel));
    }

    [Fact]
    public async Task BuildPlanAsync_NoHardwareAdapter_UsesDevice0()
    {
        // Arrange
        _adapters = new FakeAdapterProvider(RunTestData.Basic);
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(), Ct);

        // Assert
        Assert.Equal((0, (int?)null), (plan.Models["realesrgan-x4plus"].DeviceId, plan.TexconvDevice));
    }

    [Fact]
    public async Task BuildPlanAsync_OverrideAndLimit_PlansOverrideAndSetsOptions()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        var plan = await runner.BuildPlanAsync(new ForgeRunRequest(" realesr-general-x4v3 ", 5), Ct);

        // Assert
        Assert.Equal(
            ("realesr-general-x4v3", "realesr-general-x4v3", (int?)5, _session.Current!.WorkRoot, _session.Current.OutputRoot),
            (string.Join(",", plan.Models.Keys), plan.Options.ModelOverride, plan.Options.MaxJobs, plan.Options.WorkRoot, plan.Options.OutputRoot));
    }

    [Fact]
    public async Task BuildPlanAsync_ModelNotInstalled_ThrowsNamingTheModel()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.BuildPlanAsync(new ForgeRunRequest("4x-missing"), Ct));

        // Assert
        Assert.Contains("4x-missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPlanAsync_ZeroLimit_ThrowsArgumentOutOfRange()
    {
        // Arrange
        using var runner = CreateRunner();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.BuildPlanAsync(new ForgeRunRequest(MaxJobs: 0), Ct));
    }

    [Fact]
    public void ChooseVariant_NoVariantInstalled_ReturnsNull()
    {
        // Act
        var variant = ForgeRunPlanning.ChooseVariant("unknown-x4", null, [RunTestData.Model("realesrgan-x4plus", "fp32")]);

        // Assert
        Assert.Null(variant);
    }

    [Fact]
    public void FindTexconv_NoCandidateExists_ThrowsListingTriedPaths()
    {
        // Arrange
        var baseDir = Path.Combine(_root, "app");

        // Act
        var ex = Assert.Throws<FileNotFoundException>(() => ForgeRunPlanning.FindTexconv(baseDir, _ => false));

        // Assert
        Assert.Contains(Path.Combine(baseDir, "tools", "texconv.exe"), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindTexconv_OnlyRepositoryToolExists_ReturnsRepositoryTool()
    {
        // Arrange
        var repo = Path.Combine(_root, "repo");
        var baseDir = Path.Combine(repo, "src", "AetheriumForge.App", "bin", "Debug", "net10.0-windows");
        var repoTool = Path.Combine(repo, "tools", "texconv.exe");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(repo, "AetheriumForge.slnx"), repoTool };

        // Act
        var found = ForgeRunPlanning.FindTexconv(baseDir, existing.Contains);

        // Assert
        Assert.Equal(repoTool, found, ignoreCase: true);
    }

    [Fact]
    public void FindTexconv_ExecutableToolExists_PrefersExecutableTool()
    {
        // Arrange
        var baseDir = Path.Combine(_root, "app");
        var exeTool = Path.Combine(baseDir, "tools", "texconv.exe");

        // Act
        var found = ForgeRunPlanning.FindTexconv(baseDir, path => string.Equals(path, exeTool, StringComparison.OrdinalIgnoreCase));

        // Assert
        Assert.Equal(exeTool, found, ignoreCase: true);
    }

    [Fact]
    public void FindRepositoryRoot_SolutionTooFarUp_ReturnsNull()
    {
        // Arrange
        var repo = Path.Combine(_root, "repo");
        var deep = Path.Combine(repo, "a", "b", "c", "d", "e", "f", "g", "h");

        // Act
        var root = ForgeRunPlanning.FindRepositoryRoot(deep, path => string.Equals(path, Path.Combine(repo, "AetheriumForge.slnx"), StringComparison.OrdinalIgnoreCase));

        // Assert
        Assert.Null(root);
    }

    [Fact]
    public void RequiredModels_ParallaxAndNoModelJobs_AreSkipped()
    {
        // Arrange
        JobRecord[] jobs = [RunTestData.Job("a", "m1"), RunTestData.Job("b", "M1"), RunTestData.Job("c", null), RunTestData.Job("d", "m2", "Parallax")];

        // Act
        var models = ForgeRunPlanning.RequiredModels(jobs, null);

        // Assert
        Assert.Equal(["m1"], models);
    }

    private ForgeRunner CreateRunner() => new(_session, _adapters, _backend, new ChildProcessTracker(), NullLogger.Instance);
}
