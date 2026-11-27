using AetheriumForge.Cli;
using AetheriumForge.Cli.Commands;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Rendering;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class ConsoleHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"af-host-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void OnPress_FirstPress_CancelsAndKeepsProcess()
    {
        // Arrange
        var restored = 0;
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => restored++, hook: false, CancellationToken.None);

        // Act
        var keepAlive = cancellation.OnPress();

        // Assert
        Assert.Equal((true, true, 0), (keepAlive, cancellation.IsCancellationRequested, restored));
    }

    [Fact]
    public void OnPress_SecondPress_RestoresConsoleThenLetsProcessExit()
    {
        // Arrange
        var restored = 0;
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => restored++, hook: false, CancellationToken.None);
        cancellation.OnPress();

        // Act
        var keepAlive = cancellation.OnPress();

        // Assert
        Assert.Equal((false, 1), (keepAlive, restored));
    }

    [Fact]
    public async Task RunAsync_KeySourceThrows_StopsReadingKeysAndKeepsRendering()
    {
        // Arrange
        var pipeline = new FakePipeline();
        var output = new StringWriter();
        var view = new ForgeConsoleView(output, interactive: false, readKeys: true, new ThrowingKeys());
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => { }, hook: false, CancellationToken.None);
        var run = pipeline.RunAsync(view, CancellationToken.None);

        // Act
        var drawing = view.RunAsync(pipeline, run, cancellation);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        pipeline.Finish();
        await drawing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((false, true), (view.ReadsKeys, output.ToString().Contains("[Idle]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunAsync_PKey_PausesPipeline()
    {
        // Arrange
        var pipeline = new FakePipeline();
        var view = new ForgeConsoleView(TextWriter.Null, interactive: false, readKeys: true, new QueuedKeys('p'));
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => { }, hook: false, CancellationToken.None);
        var run = pipeline.RunAsync(view, CancellationToken.None);

        // Act
        var drawing = view.RunAsync(pipeline, run, cancellation);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        pipeline.Finish();
        await drawing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, pipeline.Pauses);
    }

    [Fact]
    public async Task RunWithViewAsync_ViewFails_StopsAndAwaitsPipelineBeforeRethrowing()
    {
        // Arrange: the pipeline only finishes when it is stopped; the view's writer is broken.
        var pipeline = new FakePipeline { FinishOnStop = true };
        var view = new ForgeConsoleView(new BrokenWriter(), interactive: false, readKeys: false);
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => { }, hook: false, CancellationToken.None);

        // Act
        var error = await Record.ExceptionAsync(() => ForgeCommand.RunWithViewAsync(pipeline, view, cancellation, Serilog.Core.Logger.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((typeof(ObjectDisposedException), true, true), (error?.GetType(), pipeline.Completed, cancellation.IsCancellationRequested));
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_ReturnsErrorExitCode()
    {
        // Arrange
        var app = new CliApp();
        var log = Path.Combine(_root, "x.log");
        var parse = app.Root.Parse(["status", "--log", log]);

        // Act
        var code = await CliRunner.RunAsync(parse, app.Globals, session =>
        {
            session.AttachLog(null, null);
            throw new FormatException("boom");
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ExitCodes.Error, code);
    }

    [Fact]
    public async Task RunAsync_CancelledByUser_ReturnsStoppedExitCode()
    {
        // Arrange
        var app = new CliApp();
        var parse = app.Root.Parse(["status", "--log", Path.Combine(_root, "y.log")]);

        // Act
        var code = await CliRunner.RunAsync(parse, app.Globals, session =>
        {
            session.AttachLog(null, null);
            session.Cancellation.Cancel();
            session.Cancellation.Token.ThrowIfCancellationRequested();
            return Task.FromResult(ExitCodes.Ok);
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ExitCodes.Stopped, code);
    }

    [Fact]
    public void AttachLog_LogInsideGameFolder_Throws()
    {
        // Arrange
        using var logging = CliLogging.Create(verbose: false, logFile: null);
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => { }, hook: false, CancellationToken.None);
        var session = new CliSession(TextWriter.Null, TextWriter.Null, logging, cancellation, @"D:\Games\Skyrim\Data\forge.log");

        // Act + Assert
        Assert.Throws<CliException>(() => session.AttachLog(null, @"D:\Games\Skyrim"));
    }

    [Fact]
    public async Task RunWithViewAsync_LimitReached_StopsRunWithoutUserStop()
    {
        // Arrange: the fake reports one finished job, then waits for a stop.
        var pipeline = new FakePipeline { FinishOnStop = true, DoneBeforeWaiting = 1 };
        var view = new ForgeConsoleView(TextWriter.Null, interactive: false, readKeys: false);
        using var cancellation = new ConsoleCancellation(TextWriter.Null, () => { }, hook: false, CancellationToken.None);

        // Act
        var outcome = await ForgeCommand.RunWithViewAsync(pipeline, view, cancellation, Serilog.Core.Logger.None, limit: 1)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((true, true, false), (outcome.LimitReached, outcome.Summary.Stopped, cancellation.IsCancellationRequested));
    }

    [Fact]
    public void NormalStrengthFor_KnownAndRemovedGroups_UsesRuleOrNeutral()
    {
        var rules = new AetheriumForge.Core.Rules.ForgeRules { Groups = [new AetheriumForge.Core.Rules.GroupRule { Name = "Large", Model = "m", NormalStrength = 1.4 }] };

        var strengths = (CompositionRoot.NormalStrengthFor(rules, "large"), CompositionRoot.NormalStrengthFor(rules, "gone"));

        Assert.Equal((1.4, 1.0), strengths);
    }

    private sealed class ThrowingKeys : IKeySource
    {
        public char? TryRead() => throw new InvalidOperationException("input is redirected");
    }

    private sealed class QueuedKeys(params char[] keys) : IKeySource
    {
        private readonly Queue<char> _keys = new(keys);

        public char? TryRead() => _keys.TryDequeue(out var key) ? key : null;
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new ObjectDisposedException("console");
    }

    /// <summary>A pipeline that reports one snapshot and runs until <see cref="Finish"/> (or the stop token, when <see cref="FinishOnStop"/>).</summary>
    private sealed class FakePipeline : IForgePipeline
    {
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauses;

        public bool FinishOnStop { get; init; }

        public int DoneBeforeWaiting { get; init; }

        public bool Completed { get; private set; }

        public int Pauses => Volatile.Read(ref _pauses);

        public PipelineState State { get; private set; }

        public async Task<PipelineRunSummary> RunAsync(IProgress<PipelineProgress>? progress, CancellationToken stopToken)
        {
            State = PipelineState.Running;
            progress?.Report(Snapshot(PipelineState.Running, DoneBeforeWaiting));
            using var registration = FinishOnStop ? stopToken.Register(Finish) : default;
            await _finish.Task.ConfigureAwait(false);
            State = PipelineState.Idle;
            progress?.Report(Snapshot(PipelineState.Idle, 1));
            Completed = true;
            return new PipelineRunSummary(1, 0, 0, stopToken.IsCancellationRequested, TimeSpan.FromSeconds(1));
        }

        public void Finish() => _finish.TrySetResult();

        public void Pause() => Interlocked.Increment(ref _pauses);

        public void Unpause()
        {
        }

        private static PipelineProgress Snapshot(PipelineState state, int done) =>
            new(state, done, 0, Math.Max(0, 1 - done), Math.Max(1, done), [], 0, null, TimeSpan.Zero, null);
    }
}
