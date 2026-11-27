using AetheriumForge.App.Pages.Run;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Events;
using Serilog.Parsing;

namespace AetheriumForge.Core.Tests.App.Run;

public sealed class RunViewModelTests : IDisposable
{
    private readonly FakeRunner _runner = new();
    private readonly FakeProjectSession _session = new()
    {
        Current = new ProjectInfo(@"C:\proj", @"C:\Game", @"C:\Game\Data", @"C:\proj\output", @"C:\proj\work", ForgeRules.Default),
    };

    private readonly FakeAdapterProvider _adapters = new(RunTestData.Gtx1080);
    private readonly ForgeRunnerLogSink _sink = new(TimeProvider.System, TimeSpan.FromHours(1));
    private readonly List<ModelInfo> _models = [RunTestData.Model("realesrgan-x4plus", "fp32"), RunTestData.Model("realesrgan-x4plus-fp16", "fp16"), RunTestData.Model("realesr-general-x4v3", "fp32")];

    public void Dispose() => _sink.Dispose();

    [Theory]
    [InlineData(PipelineState.Idle, true, false, false, false)]
    [InlineData(PipelineState.Running, false, true, false, true)]
    [InlineData(PipelineState.Paused, false, false, true, true)]
    [InlineData(PipelineState.Stopping, false, false, false, false)]
    public void Commands_PerState_AreEnabledByTheStateMachine(PipelineState state, bool start, bool pause, bool resume, bool stop)
    {
        // Arrange
        using var vm = CreateViewModel();

        // Act
        _runner.SetState(state);

        // Assert
        Assert.Equal(
            (start, pause, resume, stop),
            (vm.StartCommand.CanExecute(null), vm.PauseCommand.CanExecute(null), vm.ResumeCommand.CanExecute(null), vm.StopCommand.CanExecute(null)));
    }

    [Fact]
    public void StartCommand_NoProject_IsDisabled()
    {
        // Arrange
        _session.Current = null;

        // Act
        using var vm = CreateViewModel();

        // Assert
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_LimitAndOverride_PassesRequestToRunner()
    {
        // Arrange
        using var vm = CreateViewModel();
        vm.SelectedModel = vm.ModelOptions.Single(o => o.Name == "realesr-general-x4v3");
        vm.LimitEnabled = true;
        vm.JobLimit = 5;

        // Act
        vm.StartCommand.Execute(null);

        // Assert
        Assert.Equal(new ForgeRunRequest("realesr-general-x4v3", 5), _runner.LastRequest);
        _runner.Run.TrySetResult(new PipelineRunSummary(5, 0, 0, false, TimeSpan.Zero));
    }

    [Fact]
    public void StartCommand_StartFails_ShowsMessage()
    {
        // Arrange
        using var vm = CreateViewModel();
        _runner.Run.SetException(new InvalidOperationException("Model(s) not installed"));

        // Act
        vm.StartCommand.Execute(null);

        // Assert
        Assert.Equal("Model(s) not installed", vm.StatusMessage);
    }

    [Fact]
    public void PauseCommand_Running_PausesRunnerAndEnablesResume()
    {
        // Arrange
        using var vm = CreateViewModel();
        _runner.SetState(PipelineState.Running);

        // Act
        vm.PauseCommand.Execute(null);

        // Assert
        Assert.Equal((1, true), (_runner.Pauses, vm.ResumeCommand.CanExecute(null)));
    }

    [Fact]
    public void StopCommand_Paused_RequestsStop()
    {
        // Arrange
        using var vm = CreateViewModel();
        _runner.SetState(PipelineState.Paused);

        // Act
        vm.StopCommand.Execute(null);

        // Assert
        Assert.Equal((1, PipelineState.Stopping), (_runner.Stops, vm.State));
    }

    [Fact]
    public void ModelOptions_InstalledVariants_ListsJobDefaultThenFamilies()
    {
        // Act
        using var vm = CreateViewModel();

        // Assert
        Assert.Equal([null, "realesr-general-x4v3", "realesrgan-x4plus"], vm.ModelOptions.Select(o => o.Name));
    }

    [Fact]
    public void ProgressChanged_Snapshot_MapsCountsEtaElapsedAndThroughput()
    {
        // Arrange
        using var vm = CreateViewModel();
        _runner.State = PipelineState.Running;

        // Act
        _runner.Report(RunTestData.Progress());

        // Assert
        Assert.Equal(
            (3, 1, 6, 10, 40.0, "~3 min 20 s", "1:02:05", "1.25 MP/s", "boom", PipelineState.Running),
            (vm.Done, vm.Failed, vm.Remaining, vm.Total, vm.ProgressPercent, vm.EtaText, vm.ElapsedText, vm.ThroughputText, vm.LastError, vm.State));
    }

    [Fact]
    public void ProgressChanged_Snapshot_MapsStageLanes()
    {
        // Arrange
        using var vm = CreateViewModel();

        // Act
        _runner.Report(RunTestData.Progress());

        // Assert
        var upscale = vm.Lanes[(int)PipelineStage.Upscale];
        Assert.Equal(
            ("b.dds", "d.dds", "12 / 48 tiles", 25.0, StageLaneViewModel.IdleText, "0 in flight · 1 queued"),
            (vm.Lanes[0].CurrentItem, upscale.CurrentItem, upscale.TilesText, upscale.TilePercent, vm.Lanes[2].CurrentItem, vm.Lanes[3].CountsText));
    }

    [Fact]
    public void RunStateChanged_RunEnded_ShowsSummary()
    {
        // Arrange
        using var vm = CreateViewModel();

        // Act
        _runner.SetState(PipelineState.Idle, new PipelineRunSummary(4, 1, 2, true, TimeSpan.FromSeconds(65)));

        // Assert
        Assert.Contains("Stopped", vm.LastSummary, StringComparison.Ordinal);
        Assert.Contains("4 done, 1 failed, 2 returned to the queue", vm.LastSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendLog_MoreThanCapacity_KeepsNewestLines()
    {
        // Arrange
        using var vm = CreateViewModel();
        vm.AppendLog([.. Enumerable.Range(0, 300).Select(i => $"line {i}")]);

        // Act
        vm.AppendLog([.. Enumerable.Range(300, 300).Select(i => $"line {i}")]);

        // Assert
        Assert.Equal((ForgeRunnerLogSink.Capacity, "line 100", "line 599"), (vm.LogLines.Count, vm.LogLines[0], vm.LogLines[^1]));
    }

    [Fact]
    public void LogSink_FlushAfterEmit_DeliversLinesToTail()
    {
        // Arrange
        using var vm = CreateViewModel();
        var template = new MessageTemplateParser().Parse("Hello {Name}");
        var properties = new[] { new LogEventProperty("Name", new ScalarValue("forge")) };

        // Act
        _sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Warning, null, template, properties));
        _sink.Flush();

        // Assert
        Assert.EndsWith("WRN Hello \"forge\"", Assert.Single(vm.LogLines), StringComparison.Ordinal);
    }

    [Fact]
    public void LogSink_EmitBeyondCapacity_KeepsOnlyCapacityPending()
    {
        // Arrange
        var batches = new List<IReadOnlyList<string>>();
        _sink.LinesReceived += (_, lines) => batches.Add(lines);
        var template = new MessageTemplateParser().Parse("x");

        // Act
        for (var i = 0; i < ForgeRunnerLogSink.Capacity + 50; i++)
        {
            _sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null, template, []));
        }

        _sink.Flush();

        // Assert
        Assert.Equal(ForgeRunnerLogSink.Capacity, Assert.Single(batches).Count);
    }

    [Fact]
    public void ApplyGpuMemory_Reading_ShowsUsageOfBudget()
    {
        // Arrange
        using var vm = CreateViewModel();

        // Act
        vm.ApplyGpuMemory(RunTestData.Gtx1080, new AetheriumForge.Core.Gpu.VideoMemoryInfo(8UL << 30, 2UL << 30, 0, 0));

        // Assert
        Assert.Equal((25.0, "2.00 / 8.00 GiB budget (25 %)"), (vm.GpuUsagePercent, vm.GpuMemoryText));
    }

    private RunViewModel CreateViewModel() =>
        new(_runner, _session, _adapters, _sink, new InlineDispatcher(), () => _models, TimeProvider.System, gpuPollInterval: null, NullLogger.Instance);
}
