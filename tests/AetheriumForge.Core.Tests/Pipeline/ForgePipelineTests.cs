using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Tests.Pipeline;

public sealed class ForgePipelineTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", $"pipeline-{Guid.NewGuid():N}");
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkRoot => Path.Combine(_root, "work");

    private string OutputRoot => Path.Combine(_root, "out");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = await JobStore.OpenAsync(Path.Combine(_root, "project.db"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_QueuedJobs_AllDoneAndOutputsCommitted()
    {
        // Arrange
        await SeedAsync(10);
        var (pipeline, _, _, _) = Create();

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        var jobs = await AllJobsAsync();
        Assert.Equal(
            (10, 0, 0, false, 10, 10, 0),
            (summary.Done, summary.Failed, summary.Requeued, summary.Stopped, jobs.Count(j => j.State == JobState.Done), OutputFiles().Length, WorkEntries().Length));
    }

    [Fact]
    public async Task RunAsync_QueuedJobs_RecordsSecondsAndFinishTime()
    {
        // Arrange
        await SeedAsync(3);
        var (pipeline, _, _, _) = Create();

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.All(await AllJobsAsync(), j => Assert.True(j.Seconds is >= 0 && j.FinishedAt is not null && j.Error is null));
    }

    [Fact]
    public async Task RunAsync_ManyJobs_RespectsWorkerBounds()
    {
        // Arrange
        await SeedAsync(16);
        var codec = new PipelineFakeCodec { DecodeDelay = TimeSpan.FromMilliseconds(15), EncodeDelay = TimeSpan.FromMilliseconds(15) };
        var upscaler = new PipelineFakeUpscaler { Tiles = 2, TileDelay = TimeSpan.FromMilliseconds(5) };
        var (pipeline, _, _, _) = Create(codec, upscaler, Options() with { DecodeWorkers = 3, EncodeWorkers = 2, PostProcessWorkers = 2 });

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert: exactly one GPU item at a time, never more than the configured CPU/texconv workers.
        Assert.Equal((1, true, true), (upscaler.Gauge.Max, codec.DecodeGauge.Max <= 3, codec.EncodeGauge.Max <= 2));
    }

    [Fact]
    public async Task RunAsync_SlowEncode_UsesSeveralEncodeWorkers()
    {
        // Arrange
        await SeedAsync(8);
        var codec = new PipelineFakeCodec { EncodeDelay = TimeSpan.FromMilliseconds(80) };
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 1 }, Options() with { EncodeWorkers = 2 });

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal(2, codec.EncodeGauge.Max);
    }

    [Fact]
    public async Task Pause_DuringUpscale_StopsNewTilesUntilUnpause()
    {
        // Arrange
        await SeedAsync(2);
        var upscaler = new PipelineFakeUpscaler { Tiles = 40, TileDelay = TimeSpan.FromMilliseconds(10) };
        var (pipeline, _, _, _) = Create(upscaler: upscaler);
        var run = pipeline.RunAsync(null, Ct);
        await WaitUntilAsync(() => upscaler.TilesRun >= 3);

        // Act: pause, let the tile in progress finish, then watch for new tiles.
        pipeline.Pause();
        await Task.Delay(100, Ct);
        var tilesWhenPaused = upscaler.TilesRun;
        await Task.Delay(300, Ct);
        var tilesAfterWaiting = upscaler.TilesRun;
        pipeline.Unpause();
        var summary = await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        Assert.Equal((tilesWhenPaused, 2), (tilesAfterWaiting, summary.Done));
    }

    [Fact]
    public async Task Pause_BeforeFirstItem_HoldsTheProducer()
    {
        // Arrange
        await SeedAsync(3);
        var codec = new PipelineFakeCodec();
        var (pipeline, _, _, _) = Create(codec);
        var progress = new RecordingProgress<PipelineProgress>();
        var run = pipeline.RunAsync(progress, Ct);
        pipeline.Pause();

        // Act
        await Task.Delay(300, Ct);
        var committedWhilePaused = codec.CommitCount;
        pipeline.Unpause();
        await run.WaitAsync(WaitTimeout, Ct);

        // Assert: at most an item already past its pause check can finish while paused.
        Assert.True(committedWhilePaused <= 1, $"{committedWhilePaused} items committed while paused");
    }

    [Fact]
    public async Task RunAsync_StoppedAfterFirstCommit_ReturnsStoppedSummary()
    {
        // Arrange
        await SeedAsync(12);
        var codec = new PipelineFakeCodec();
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 20, TileDelay = TimeSpan.FromMilliseconds(10) });
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => OutputFiles().Length >= 1);

        // Act
        await stop.CancelAsync();
        var summary = await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        Assert.Equal((true, true, true), (summary.Stopped, summary.Done >= 1 && summary.Done < 12, summary.Requeued >= 1));
    }

    [Fact]
    public async Task RunAsync_StoppedMidRun_LeavesConsistentDatabaseAndOutput()
    {
        // Arrange
        await SeedAsync(12);
        var codec = new PipelineFakeCodec();
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 20, TileDelay = TimeSpan.FromMilliseconds(10) });
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => OutputFiles().Length >= 1);

        // Act
        await stop.CancelAsync();
        await run.WaitAsync(WaitTimeout, Ct);

        // Assert: every committed file is a Done job and vice versa, nothing Running, attempts untouched, work folder empty.
        var jobs = await AllJobsAsync();
        var done = jobs.Where(j => j.State == JobState.Done).Select(j => Path.Combine(OutputRoot, j.Path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(
            (string.Join('|', done), 12 - done.Length, 0, 0),
            (string.Join('|', OutputFiles().Order(StringComparer.OrdinalIgnoreCase)), jobs.Count(j => j.State == JobState.Queued), jobs.Sum(j => j.Attempts), WorkEntries().Length));
    }

    [Fact]
    public async Task RunAsync_StoppedDuringEncode_CommitsNothingPartial()
    {
        // Arrange: a slow encoder so the stop lands while files are being written.
        await SeedAsync(4);
        var codec = new PipelineFakeCodec { EncodeDelay = TimeSpan.FromSeconds(10) };
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 1 });
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => Directory.Exists(WorkRoot) && Directory.EnumerateFiles(WorkRoot, "encode.tmp", SearchOption.AllDirectories).Any());

        // Act
        await stop.CancelAsync();
        var summary = await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        Assert.Equal((0, 0, 0, 4), (summary.Done, OutputFiles().Length, WorkEntries().Length, (await AllJobsAsync()).Count(j => j.State == JobState.Queued)));
    }

    [Fact]
    public async Task RunAsync_SecondRunAfterStop_FinishesRestWithoutDuplicateWork()
    {
        // Arrange
        await SeedAsync(12);
        var codec = new PipelineFakeCodec();
        var upscaler = new PipelineFakeUpscaler { Tiles = 20, TileDelay = TimeSpan.FromMilliseconds(5) };
        var (first, _, _, _) = Create(codec, upscaler);
        using var stop = new CancellationTokenSource();
        var run = first.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => OutputFiles().Length >= 2);
        await stop.CancelAsync();
        await run.WaitAsync(WaitTimeout, Ct);
        var doneAfterFirst = (await AllJobsAsync()).Where(j => j.State == JobState.Done).Select(j => j.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (second, secondSource, _, _) = Create(codec, upscaler);

        // Act
        var summary = await second.RunAsync(null, Ct);

        // Assert: the second run touches only what the first did not finish, each once; all Done; every file complete.
        var jobs = await AllJobsAsync();
        Assert.Equal(
            (12, false, 12 - doneAfterFirst.Count, true, 12, true),
            (jobs.Count(j => j.State == JobState.Done), secondSource.Reads.Keys.Any(doneAfterFirst.Contains), secondSource.Reads.Count, secondSource.Reads.Values.All(n => n == 1),
                OutputFiles().Length, OutputFiles().All(f => File.ReadAllBytes(f).SequenceEqual(PipelineFakeCodec.EncodedBytes))));
        Assert.False(summary.Stopped);
    }

    [Fact]
    public async Task RunAsync_AfterCrash_RequeuesRunningJobsAndWipesWorkRoot()
    {
        // Arrange: a previous process died with jobs Running and junk in the work folder.
        await SeedAsync(4);
        foreach (var job in (await AllJobsAsync()).Take(2))
        {
            await _store.MarkRunningAsync(job.Id, Ct);
        }

        Directory.CreateDirectory(Path.Combine(WorkRoot, "deadbeef"));
        await File.WriteAllTextAsync(Path.Combine(WorkRoot, "deadbeef", "source.dds"), "junk", Ct);
        await File.WriteAllTextAsync(Path.Combine(WorkRoot, "stray.tmp"), "junk", Ct);
        var (pipeline, _, _, _) = Create();

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal((4, 4, 0), (summary.Done, (await AllJobsAsync()).Count(j => j.State == JobState.Done), WorkEntries().Length));
    }

    [Fact]
    public async Task RunAsync_StopRequestedBeforeStart_RecoversAndReturnsStopped()
    {
        // Arrange
        await SeedAsync(2);
        await _store.MarkRunningAsync((await AllJobsAsync())[0].Id, Ct);
        var (pipeline, _, _, _) = Create();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        // Act
        var summary = await pipeline.RunAsync(null, stop.Token);

        // Assert
        Assert.Equal((true, 0, 2), (summary.Stopped, summary.Done, (await AllJobsAsync()).Count(j => j.State == JobState.Queued)));
    }

    [Fact]
    public async Task RunAsync_FailingJob_RetriedThenFailedAndRunContinues()
    {
        // Arrange
        await SeedAsync(4);
        await SeedAsync(1, $"{PipelineFakeSource.FailMarker}_");
        var (pipeline, source, _, _) = Create(options: Options() with { MaxAttempts = 2 });

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        var broken = (await AllJobsAsync()).Single(j => j.Path.Contains(PipelineFakeSource.FailMarker, StringComparison.Ordinal));
        Assert.Equal(
            (4, 1, JobState.Failed, 2, 2, true),
            (summary.Done, summary.Failed, broken.State, broken.Attempts, source.Reads[broken.Path], broken.Error!.Contains("cannot read", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunAsync_UnknownKind_FailsJobWithoutProcessing()
    {
        // Arrange
        await _store.UpsertJobsAsync([Record(@"textures\t\odd.dds") with { Kind = "Hologram" }], Ct);
        var (pipeline, source, _, _) = Create();

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal((1, 0, JobState.Failed), (summary.Failed, source.Reads.Count, (await AllJobsAsync())[0].State));
    }

    [Fact]
    public async Task RunAsync_WithProgress_ThrottlesReports()
    {
        // Arrange
        await SeedAsync(6);
        var interval = TimeSpan.FromMilliseconds(50);
        var (pipeline, _, _, _) = Create(upscaler: new PipelineFakeUpscaler { Tiles = 10, TileDelay = TimeSpan.FromMilliseconds(5) }, options: Options() with { ProgressInterval = interval });
        var progress = new RecordingProgress<PipelineProgress>();
        var started = DateTime.UtcNow;

        // Act
        await pipeline.RunAsync(progress, Ct);

        // Assert: at most one report per interval, plus the final report.
        var allowed = ((DateTime.UtcNow - started) / interval) + 2;
        Assert.InRange(progress.Reports.Count, 1, allowed);
    }

    [Fact]
    public async Task RunAsync_WithProgress_SendsFinalReport()
    {
        // Arrange
        await SeedAsync(5);
        var (pipeline, _, _, _) = Create();
        var progress = new RecordingProgress<PipelineProgress>();

        // Act
        await pipeline.RunAsync(progress, Ct);

        // Assert
        var last = progress.Reports[^1].Value;
        Assert.Equal((PipelineState.Idle, 5, 0, 0, 5, TimeSpan.Zero, 4), (last.State, last.Done, last.Failed, last.Remaining, last.Total, last.Eta, last.Stages.Count));
    }

    [Fact]
    public async Task RunAsync_WithProgress_ReportsTilesAndEta()
    {
        // Arrange
        await SeedAsync(3);
        var upscaler = new PipelineFakeUpscaler { Tiles = 30, TileDelay = TimeSpan.FromMilliseconds(5) };
        var (pipeline, _, _, _) = Create(upscaler: upscaler, options: Options() with { ProgressInterval = TimeSpan.FromMilliseconds(10) });
        var progress = new RecordingProgress<PipelineProgress>();

        // Act
        await pipeline.RunAsync(progress, Ct);

        // Assert: some report shows the upscaler mid-image with a path and tile counts, and a positive ETA for remaining work.
        var reports = progress.Reports.Select(r => r.Value).ToArray();
        Assert.True(
            reports.Any(p => p.Stages[(int)PipelineStage.Upscale] is { CurrentPath: not null, InFlight: 1, TilesTotal: 30, TilesDone: > 0 })
            && reports.Any(p => p.Remaining > 0 && p.Eta > TimeSpan.Zero),
            "expected tile progress and a positive ETA");
    }

    [Fact]
    public async Task RunAsync_FinishedJobs_RecordedInEtaWithModelOrLanczos()
    {
        // Arrange
        await SeedAsync(2);
        await _store.UpsertJobsAsync([Record(@"textures\t\height_p.dds") with { Kind = nameof(TextureKind.Parallax) }], Ct);
        var (pipeline, _, _, eta) = Create();

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal(["fake", "fake", ForgePipeline.NoModelKey], eta.Records.Select(r => r.Model).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RunAsync_Throughput_ReportsSourceMegapixelsPerSecond()
    {
        // Arrange
        await SeedAsync(4);
        var (pipeline, _, _, _) = Create();
        var progress = new RecordingProgress<PipelineProgress>();

        // Act
        await pipeline.RunAsync(progress, Ct);

        // Assert
        Assert.True(progress.Reports[^1].Value.SourceMegapixelsPerSecond > 0);
    }

    [Fact]
    public async Task State_DuringRun_FollowsRunningPausedStoppingIdle()
    {
        // Arrange
        await SeedAsync(3);
        var upscaler = new PipelineFakeUpscaler { Tiles = 200, TileDelay = TimeSpan.FromMilliseconds(5) };
        var (pipeline, _, _, _) = Create(upscaler: upscaler);
        using var stop = new CancellationTokenSource();
        var states = new List<PipelineState> { pipeline.State };

        // Act
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => upscaler.TilesRun > 0);
        states.Add(pipeline.State);
        pipeline.Pause();
        states.Add(pipeline.State);
        pipeline.Unpause();
        states.Add(pipeline.State);
        await stop.CancelAsync();
        states.Add(pipeline.State);
        await run.WaitAsync(WaitTimeout, Ct);
        states.Add(pipeline.State);

        // Assert
        Assert.Equal([PipelineState.Idle, PipelineState.Running, PipelineState.Paused, PipelineState.Running, PipelineState.Stopping, PipelineState.Idle], states);
    }

    [Fact]
    public void Pause_WhenIdle_DoesNothing()
    {
        // Arrange
        var (pipeline, _, _, _) = Create();

        // Act
        pipeline.Pause();
        pipeline.Unpause();
        pipeline.Pause();

        // Assert
        Assert.Equal(PipelineState.Idle, pipeline.State);
    }

    [Fact]
    public async Task RunAsync_WhileRunning_ThrowsInvalidOperationException()
    {
        // Arrange
        await SeedAsync(2);
        var upscaler = new PipelineFakeUpscaler { Tiles = 50, TileDelay = TimeSpan.FromMilliseconds(5) };
        var (pipeline, _, _, _) = Create(upscaler: upscaler);
        var run = pipeline.RunAsync(null, Ct);

        // Act
        var second = () => pipeline.RunAsync(null, Ct);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(second);
        await run.WaitAsync(WaitTimeout, Ct);
    }

    [Fact]
    public async Task RunAsync_AfterPreviousRunEnded_CanRunAgain()
    {
        // Arrange
        await SeedAsync(1);
        var (pipeline, _, _, _) = Create();
        await pipeline.RunAsync(null, Ct);
        await SeedAsync(1, "more_");

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal(1, summary.Done);
    }

    [Fact]
    public void Constructor_WorkRootDiffersFromProcessor_ThrowsArgumentException()
    {
        // Arrange
        var processor = new TextureProcessor(new PipelineFakeSource(), new PipelineFakeCodec(), _ => new PipelineFakeUpscaler(), OutputRoot, WorkRoot);
        var options = Options() with { WorkRoot = Path.Combine(_root, "elsewhere") };

        // Act
        var act = () => new ForgePipeline(_store, processor, new FakeEtaEstimator(), options);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Constructor_OutputInsideWorkRoot_ThrowsArgumentException()
    {
        // Arrange: wiping the work folder would delete the output.
        var output = Path.Combine(WorkRoot, "out");
        var processor = new TextureProcessor(new PipelineFakeSource(), new PipelineFakeCodec(), _ => new PipelineFakeUpscaler(), output, WorkRoot);
        var options = Options() with { OutputRoot = output };

        // Act
        var act = () => new ForgePipeline(_store, processor, new FakeEtaEstimator(), options);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void ToTextureJob_WideSource_KeepsAspectRatio()
    {
        // Arrange
        var record = Record(@"textures\t\wide.dds") with { SourceWidth = 512, SourceHeight = 256, Target = 2048, Kind = "normal", Group = "Clutter" };

        // Act
        var job = ForgePipeline.ToTextureJob(record, group => group == "Clutter" ? 1.4 : 1.0);

        // Assert
        Assert.Equal((2048, 1024, TextureKind.Normal, 1.4, (int?)null), (job.TargetWidth, job.TargetHeight, job.Kind, job.NormalStrength, job.SourceDxgiFormat));
    }

    [Fact]
    public void ToTextureJob_LookForGroup_SetsLookAndItsNormalStrength()
    {
        // Arrange
        var look = LookSettings.Neutral with { NormalStrength = 2.2, Saturation = 1.3 };
        var record = Record(@"textures\t\look.dds") with { Kind = "normal", Group = "Clutter" };

        // Act
        var job = ForgePipeline.ToTextureJob(record, _ => 1.4, group => group == "Clutter" ? look : null);

        // Assert
        Assert.Equal((2.2, look), (job.NormalStrength, job.EffectiveLook));
    }

    [Fact]
    public void ToTextureJob_LookForGroupReturnsNull_FallsBackToNormalStrength()
    {
        // Arrange
        var record = Record(@"textures\t\look.dds") with { Group = "Clutter" };

        // Act
        var job = ForgePipeline.ToTextureJob(record, _ => 1.4, _ => null);

        // Assert
        Assert.Equal(LookSettings.Neutral with { NormalStrength = 1.4 }, job.EffectiveLook);
    }

    [Fact]
    public async Task RunAsync_LookForGroup_StoresEffectiveLookHashOnDoneJobs()
    {
        // Arrange
        var look = LookSettings.Neutral with { Contrast = 1.2 };
        await _store.UpsertJobsAsync(
            [Record(@"textures\t\a.dds") with { Group = "Clutter" }, Record(@"textures\t\b.dds") with { Group = "Large" }],
            Ct);
        var processor = new TextureProcessor(new PipelineFakeSource(), new PipelineFakeCodec(), _ => new PipelineFakeUpscaler(), OutputRoot, WorkRoot);
        var pipeline = new ForgePipeline(_store, processor, new FakeEtaEstimator(), Options())
        {
            NormalStrengthForGroup = _ => 1.0,
            LookForGroup = group => group == "Clutter" ? look : null,
        };

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        var hashes = (await AllJobsAsync()).OrderBy(j => j.Path).Select(j => j.LookHash);
        Assert.Equal([look.GetStableHash(), LookSettings.Neutral.GetStableHash()], hashes);
    }

    [Fact]
    public void ToTextureJob_OddAspect_RoundsLikeRuleEvaluator()
    {
        // Arrange
        var record = Record(@"textures\t\odd.dds") with { SourceWidth = 300, SourceHeight = 1000, Target = 1024 };

        // Act
        var job = ForgePipeline.ToTextureJob(record);

        // Assert: 300 × 1024 / 1000 = 307.2 → 307.
        Assert.Equal((307, 1024), (job.TargetWidth, job.TargetHeight));
    }

    [Theory]
    [InlineData("Hologram", 8, 8, 32)]
    [InlineData("7", 8, 8, 32)]
    [InlineData("Diffuse", 0, 8, 32)]
    [InlineData("Diffuse", 8, 8, 0)]
    public void ToTextureJob_InvalidRow_ThrowsInvalidDataException(string kind, int width, int height, int target)
    {
        // Arrange
        var record = Record(@"textures\t\bad.dds") with { Kind = kind, SourceWidth = width, SourceHeight = height, Target = target };

        // Act
        var act = () => ForgePipeline.ToTextureJob(record);

        // Assert
        Assert.Throws<InvalidDataException>(act);
    }

    // Review round 1 (PR #11): one test per item.

    [Fact]
    public async Task RunAsync_ProgressHandlerThrows_RunCompletes()
    {
        // Arrange (item 1)
        await SeedAsync(4);
        var (pipeline, _, _, _) = Create(options: Options() with { ProgressInterval = TimeSpan.FromMilliseconds(5) });

        // Act
        var summary = await pipeline.RunAsync(new ThrowingProgress(), Ct);

        // Assert
        Assert.Equal((4, PipelineState.Idle), (summary.Done, pipeline.State));
    }

    [Fact]
    public async Task RunAsync_PathIntoStagingFolder_FailsInProducerWithoutDecodeOrAttempt()
    {
        // Arrange (item 2): the path passes the row checks but GetOutputPath rejects it.
        await _store.UpsertJobsAsync([Record($@"{TextureProcessor.StagingFolderName}\evil.dds")], Ct);
        var (pipeline, source, _, _) = Create();

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert
        var job = (await AllJobsAsync())[0];
        Assert.Equal((1, 0, JobState.Failed, 0), (summary.Failed, source.Reads.Count, job.State, job.Attempts));
    }

    [Fact]
    public async Task RunAsync_MarkDoneFailsAfterCommit_CountsDoneAndKeepsRunAlive()
    {
        // Arrange (item 3): the job row disappears between the encode and MarkDone, so every MarkDone throws.
        await SeedAsync(1);
        var codec = new PipelineFakeCodec { OnEncoded = _ => DeleteJobRows() };
        var (pipeline, source, _, _) = Create(codec);

        // Act
        var summary = await pipeline.RunAsync(null, Ct);

        // Assert: committed once, counted Done, not retried, no texture or work folder left behind.
        Assert.Equal((1, 0, 1, 1, 0), (summary.Done, summary.Failed, source.Reads.Values.Sum(), OutputFiles().Length, WorkEntries().Length));
    }

    [Fact]
    public async Task RunAsync_StopKillsEncodeOnLastAttempt_RequeuesInsteadOfFailing()
    {
        // Arrange (item 4): with one attempt, a stop that makes the encoder throw a non-cancellation error.
        await SeedAsync(2);
        var codec = new PipelineFakeCodec { EncodeDelay = TimeSpan.FromSeconds(10), ThrowIOExceptionWhenCancelled = true };
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 1 }, Options() with { MaxAttempts = 1 });
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => !codec.EncodeTargets.IsEmpty);

        // Act
        await stop.CancelAsync();
        var summary = await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        var jobs = await AllJobsAsync();
        Assert.Equal((0, 2, 0), (summary.Failed, jobs.Count(j => j.State == JobState.Queued), jobs.Sum(j => j.Attempts)));
    }

    [Fact]
    public async Task RunAsync_ManyJobsSeveralModels_EtaGetsOneEntryPerModel()
    {
        // Arrange (item 5): 20 model jobs and 5 parallax jobs are two models, not 25 entries.
        await SeedAsync(20);
        await _store.UpsertJobsAsync(Enumerable.Range(0, 5).Select(i => Record($@"textures\t\h{i}_p.dds") with { Kind = nameof(TextureKind.Parallax) }), Ct);
        var eta = new FakeEtaEstimator();
        var (pipeline, _, _, _) = Create(upscaler: new PipelineFakeUpscaler { Tiles = 3, TileDelay = TimeSpan.FromMilliseconds(5) }, options: Options() with { ProgressInterval = TimeSpan.FromMilliseconds(5) }, eta: eta);

        // Act
        await pipeline.RunAsync(new RecordingProgress<PipelineProgress>(), Ct);

        // Assert
        Assert.InRange(eta.MaxEntries, 1, 2);
    }

    [Fact]
    public async Task RunAsync_EveryEncodeFailsWithTinyChannels_FinishesWithoutDeadlock()
    {
        // Arrange (items 6 and 7): failures retried from the last stage while every channel is full.
        await SeedAsync(10);
        var codec = new PipelineFakeCodec { FailAllEncodes = true };
        var options = Options() with { InputCapacity = 1, StageCapacity = 1, DecodeWorkers = 1, PostProcessWorkers = 1, EncodeWorkers = 1, MaxAttempts = 3 };
        var (pipeline, _, _, _) = Create(codec, options: options);

        // Act
        var summary = await pipeline.RunAsync(null, Ct).WaitAsync(WaitTimeout, Ct);

        // Assert: all failed after three attempts each, and the attempts are persisted.
        var jobs = await AllJobsAsync();
        Assert.Equal((10, 30, true), (summary.Failed, codec.EncodeTargets.Count, jobs.All(j => j is { State: JobState.Failed, Attempts: 3 })));
    }

    [Fact]
    public async Task RunAsync_RescanSkipsJobDuringRun_JobNotProcessed()
    {
        // Arrange (item 8): the run loads 4 jobs, then a rescan drops two of them before they are taken.
        await SeedAsync(4);
        var (pipeline, source, _, _) = Create();
        var progress = new RecordingProgress<PipelineProgress>();
        var run = pipeline.RunAsync(progress, Ct);
        pipeline.Pause();
        await WaitUntilAsync(() => progress.Reports.Count > 0);
        var keep = (await AllJobsAsync()).Take(2).Select(j => j.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _store.ApplyRescanAsync(keep, Ct);

        // Act
        pipeline.Unpause();
        var summary = await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        var jobs = await AllJobsAsync();
        Assert.Equal(
            (2, 2, true, 2),
            (summary.Done, jobs.Count(j => j.State == JobState.Skipped), source.Reads.Keys.All(keep.Contains), progress.Reports[^1].Value.Total));
    }

    [Fact]
    public async Task RunAsync_Commit_StagesOnOutputVolumeThenMoves()
    {
        // Arrange (item 9)
        await SeedAsync(3);
        var codec = new PipelineFakeCodec();
        var (pipeline, _, _, _) = Create(codec);
        var staging = Path.Combine(OutputRoot, TextureProcessor.StagingFolderName) + Path.DirectorySeparatorChar;

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert: the codec only ever wrote into the staging folder; outputs are in place and staging is empty.
        Assert.Equal(
            (true, 3, 0),
            (codec.EncodeTargets.All(t => t.StartsWith(staging, StringComparison.OrdinalIgnoreCase)), OutputFiles().Length, StagingEntries().Length));
    }

    [Fact]
    public async Task RunAsync_LeftoverStagingFiles_WipedAtStart()
    {
        // Arrange (item 9): a crash left an uncommitted staged file.
        await SeedAsync(1);
        var staging = Path.Combine(OutputRoot, TextureProcessor.StagingFolderName);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "deadbeef.dds"), "partial", Ct);
        var (pipeline, _, _, _) = Create();

        // Act
        await pipeline.RunAsync(null, Ct);

        // Assert
        Assert.Equal((0, 1), (StagingEntries().Length, OutputFiles().Length));
    }

    [Fact]
    public async Task RunAsync_StoppedDuringEncode_LeavesStagingEmpty()
    {
        // Arrange (item 9)
        await SeedAsync(2);
        var codec = new PipelineFakeCodec { EncodeDelay = TimeSpan.FromSeconds(10) };
        var (pipeline, _, _, _) = Create(codec, new PipelineFakeUpscaler { Tiles = 1 });
        using var stop = new CancellationTokenSource();
        var run = pipeline.RunAsync(null, stop.Token);
        await WaitUntilAsync(() => !codec.EncodeTargets.IsEmpty);

        // Act
        await stop.CancelAsync();
        await run.WaitAsync(WaitTimeout, Ct);

        // Assert
        Assert.Equal((0, 0), (StagingEntries().Length, OutputFiles().Length));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(5, Ct);
        }
    }

    private static JobRecord Record(string path) =>
        new(0, path, "Skyrim - Textures0.bsa", 8, 8, "BC1_UNORM", nameof(TextureKind.Diffuse), null, 32, "fake", JobState.Queued, 0, null, null, null);

    private PipelineOptions Options() => new()
    {
        WorkRoot = WorkRoot,
        OutputRoot = OutputRoot,
        DecodeWorkers = 2,
        PostProcessWorkers = 2,
        EncodeWorkers = 2,
        ProgressInterval = TimeSpan.FromMilliseconds(20),
    };

    private (ForgePipeline Pipeline, PipelineFakeSource Source, PipelineFakeCodec Codec, FakeEtaEstimator Eta) Create(
        PipelineFakeCodec? codec = null,
        PipelineFakeUpscaler? upscaler = null,
        PipelineOptions? options = null,
        FakeEtaEstimator? eta = null)
    {
        var source = new PipelineFakeSource();
        codec ??= new PipelineFakeCodec();
        upscaler ??= new PipelineFakeUpscaler();
        eta ??= new FakeEtaEstimator();
        var processor = new TextureProcessor(source, codec, _ => upscaler, OutputRoot, WorkRoot);
        return (new ForgePipeline(_store, processor, eta, options ?? Options()), source, codec, eta);
    }

    private async Task SeedAsync(int count, string prefix = "tex_") =>
        await _store.UpsertJobsAsync(Enumerable.Range(0, count).Select(i => Record($@"textures\t\{prefix}{i:D3}.dds")), Ct);

    private async Task<List<JobRecord>> AllJobsAsync()
    {
        var jobs = new List<JobRecord>();
        await foreach (var job in _store.GetJobsAsync(null, Ct))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>Committed textures (everything under <c>textures\</c>; the hidden staging folder is not output).</summary>
    private string[] OutputFiles()
    {
        var textures = Path.Combine(OutputRoot, "textures");
        return Directory.Exists(textures) ? Directory.GetFiles(textures, "*", SearchOption.AllDirectories) : [];
    }

    private string[] StagingEntries()
    {
        var staging = Path.Combine(OutputRoot, TextureProcessor.StagingFolderName);
        return Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging) : [];
    }

    /// <summary>Deletes every job row behind the store's back, so the next MarkDone fails (synchronous: runs inside the fake codec).</summary>
    private void DeleteJobRows()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "project.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM job;";
        command.ExecuteNonQuery();
    }

    private string[] WorkEntries() => Directory.Exists(WorkRoot) ? Directory.GetFileSystemEntries(WorkRoot) : [];

    private sealed class ThrowingProgress : IProgress<PipelineProgress>
    {
        public void Report(PipelineProgress value) => throw new InvalidOperationException("broken progress handler");
    }
}
