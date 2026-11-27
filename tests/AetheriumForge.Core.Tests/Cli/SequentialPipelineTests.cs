using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class SequentialPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAsync_QueuedJobs_MarksAllDoneAndWritesOutputs()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 3);
        var pipeline = fixture.CreatePipeline();

        // Act
        var summary = await pipeline.RunAsync(null, TestContext.Current.CancellationToken);

        // Assert
        var counts = await fixture.CountsAsync();
        var outputs = Directory.EnumerateFiles(fixture.OutputRoot, "*.dds", SearchOption.AllDirectories).Count();
        Assert.Equal((3, 0, false, 3, 0, 3), (summary.Done, summary.Failed, summary.Stopped, counts[JobState.Done], counts[JobState.Queued], outputs));
    }

    [Fact]
    public async Task RunAsync_WithLimit_ProcessesOnlyThatManyJobs()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 3);
        var pipeline = fixture.CreatePipeline(maxJobs: 2);

        // Act
        var summary = await pipeline.RunAsync(null, TestContext.Current.CancellationToken);

        // Assert
        var counts = await fixture.CountsAsync();
        Assert.Equal((2, false, 2, 1), (summary.Done, summary.Stopped, counts[JobState.Done], counts[JobState.Queued]));
    }

    [Fact]
    public async Task RunAsync_StopDuringJob_RequeuesItAndCommitsNothing()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Upscaler.OnCall = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        };
        var pipeline = fixture.CreatePipeline();
        using var stop = new CancellationTokenSource();

        // Act
        var run = pipeline.RunAsync(null, stop.Token);
        await entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        var summary = await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Assert
        var counts = await fixture.CountsAsync();
        var outputs = Directory.Exists(fixture.OutputRoot) ? Directory.EnumerateFiles(fixture.OutputRoot, "*", SearchOption.AllDirectories).Count() : 0;
        Assert.Equal((true, 0, 1, 2, 0, 0), (summary.Stopped, summary.Done, summary.Requeued, counts[JobState.Queued], counts[JobState.Running], outputs));
    }

    [Fact]
    public async Task Pause_DuringJob_HoldsProgressUntilUnpause()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Upscaler.OnCall = async (call, _, ct) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var pipeline = fixture.CreatePipeline();

        // Act
        var run = pipeline.RunAsync(null, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        pipeline.Pause();
        release.SetResult();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var whilePaused = (pipeline.State, (await fixture.CountsAsync())[JobState.Done], fixture.Upscaler.Calls);
        pipeline.Unpause();
        var summary = await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(((PipelineState.Paused, 0, 1), 2, PipelineState.Idle), (whilePaused, summary.Done, pipeline.State));
    }

    [Fact]
    public async Task RunAsync_AfterCrash_RequeuesRunningJobsAndWipesWorkFolder()
    {
        // Arrange: a job left Running and a stale temporary folder, as after a power loss.
        await using var fixture = await PipelineFixture.CreateAsync(queued: 1, state: JobState.Running);
        var stale = Path.Combine(fixture.WorkRoot, "stale", "source.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale, "partial", TestContext.Current.CancellationToken);
        var pipeline = fixture.CreatePipeline();

        // Act
        var summary = await pipeline.RunAsync(null, TestContext.Current.CancellationToken);

        // Assert
        var counts = await fixture.CountsAsync();
        Assert.Equal((1, 1, false), (summary.Done, counts[JobState.Done], File.Exists(stale)));
    }

    [Fact]
    public async Task RunAsync_JobKeepsFailing_RetriesThenMarksFailed()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 1);
        fixture.Codec.DecodeFailure = () => new InvalidDataException("bad dds");
        var pipeline = fixture.CreatePipeline(maxAttempts: 2);
        var progress = new RecordingProgress();

        // Act
        var summary = await pipeline.RunAsync(progress, TestContext.Current.CancellationToken);

        // Assert
        var failed = await fixture.Store.GetJobsAsync(JobState.Failed, TestContext.Current.CancellationToken).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, 2, "bad dds", true), (summary.Failed, fixture.Codec.Decodes, failed.Error, progress.Snapshots[^1].LastError?.Contains("bad dds", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunAsync_Progress_ReportsUpscaleTilesAndFinalCounts()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 2);
        var pipeline = fixture.CreatePipeline();
        var progress = new RecordingProgress();

        // Act
        await pipeline.RunAsync(progress, TestContext.Current.CancellationToken);

        // Assert
        var sawTiles = progress.Snapshots.Any(s => s.Stages.Any(a => a is { Stage: PipelineStage.Upscale, TilesTotal: 1, CurrentPath: not null }));
        var last = progress.Snapshots[^1];
        Assert.Equal((true, PipelineState.Idle, 2, 0, 2, (TimeSpan?)null), (sawTiles, last.State, last.Done, last.Remaining, last.Total, last.Eta));
    }

    [Fact]
    public async Task RunAsync_WhileRunning_Throws()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Upscaler.OnCall = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var pipeline = fixture.CreatePipeline();
        var run = pipeline.RunAsync(null, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Act
        var second = await Record.ExceptionAsync(() => pipeline.RunAsync(null, TestContext.Current.CancellationToken));
        release.SetResult();
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InvalidOperationException>(second);
    }

    [Fact]
    public async Task RunAsync_LockedWorkLeftover_StillRuns()
    {
        // Arrange: a leftover file another process holds open cannot be deleted.
        await using var fixture = await PipelineFixture.CreateAsync(queued: 1);
        Directory.CreateDirectory(fixture.WorkRoot);
        var locked = Path.Combine(fixture.WorkRoot, "locked.tmp");
        var pipeline = fixture.CreatePipeline();
        PipelineRunSummary summary;
        await using (var handle = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            summary = await pipeline.RunAsync(null, TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal((1, 0), (summary.Done, summary.Failed));
    }

    [Fact]
    public async Task RunAsync_MarkDoneFailsTransiently_RetriesAndNeverMarksFailed()
    {
        // Arrange
        await using var fixture = await PipelineFixture.CreateAsync(queued: 1);
        var calls = 0;
        var pipeline = fixture.CreatePipeline(markDone: async (id, seconds) =>
        {
            if (++calls < 3)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
            }

            await fixture.Store.MarkDoneAsync(id, seconds);
        });

        // Act
        var summary = await pipeline.RunAsync(null, TestContext.Current.CancellationToken);

        // Assert
        var counts = await fixture.CountsAsync();
        Assert.Equal((1, 0, 1, 0, 3, 1), (summary.Done, summary.Failed, counts[JobState.Done], counts[JobState.Failed], calls, fixture.Upscaler.Calls));
    }
}
