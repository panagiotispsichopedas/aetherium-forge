using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class CompareRunLockTests : IDisposable
{
    private readonly CompareFakeData _data = new();
    private readonly CompareFakeDialogs _dialogs = new();
    private readonly CompareFakeRunner _runner = new();
    private readonly PreviewEngine _engine;

    public CompareRunLockTests() => _engine = new PreviewEngine(new CompareFakeBackend(), () => _runner.State);

    public void Dispose() => _engine.Dispose();

    private CompareViewModel ViewModel() => new(_engine, _data, _dialogs, new Random(1), runner: _runner);

    [Fact]
    public void IsRunActive_RunnerStarts_LocksApplyAndModelCommands()
    {
        // Arrange
        var viewModel = ViewModel();

        // Act
        _runner.SetState(PipelineState.Running);

        // Assert
        Assert.Equal((true, false, false, false), (viewModel.IsRunActive, viewModel.IsGpuAvailable, viewModel.ApplyToGroupCommand.CanExecute(null), viewModel.UseModelForGroupCommand.CanExecute(null)));
    }

    [Fact]
    public void IsRunActive_RunEnds_UnlocksApply()
    {
        // Arrange
        var viewModel = ViewModel();
        _runner.SetState(PipelineState.Paused);

        // Act
        _runner.SetState(PipelineState.Idle);

        // Assert
        Assert.Equal((false, true), (viewModel.IsRunActive, viewModel.ApplyToGroupCommand.CanExecute(null)));
    }

    [Fact]
    public async Task ApplyToGroupAsync_RunActive_ShowsReasonAndDoesNotSave()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter"));
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        _runner.SetState(PipelineState.Running);

        // Act
        await viewModel.ApplyToGroupAsync();

        // Assert
        Assert.Equal((0, CompareViewModel.RunActiveReason, 0), (_data.Saves, viewModel.ErrorMessage, _dialogs.Questions.Count));
    }

    [Fact]
    public async Task PreviewRandomAsync_RunActive_DoesNotUpscale()
    {
        // Arrange
        var backend = new CompareFakeBackend();
        using var engine = new PreviewEngine(backend, () => _runner.State);
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter", JobState.Queued));
        var viewModel = new CompareViewModel(engine, _data, _dialogs, new Random(1), runner: _runner);
        await viewModel.RefreshAsync();
        _runner.SetState(PipelineState.Running);

        // Act
        await viewModel.PreviewRandomAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((0, PreviewEngine.GpuBusyReason), (backend.Upscaler.Calls, viewModel.Status));
    }

    [Fact]
    public async Task RefreshAsync_RulesUnreadable_ShowsErrorBanner()
    {
        // Arrange
        _data.LoadFailure = new IOException("disk gone");
        var viewModel = ViewModel();

        // Act
        await viewModel.RefreshAsync();

        // Assert
        Assert.Equal((true, "Could not load the project's rules: disk gone"), (viewModel.HasError, viewModel.ErrorMessage));
    }

    [Fact]
    public async Task DismissError_AfterError_HidesBanner()
    {
        // Arrange
        _data.LoadFailure = new IOException("disk gone");
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();

        // Act
        viewModel.DismissError();

        // Assert
        Assert.False(viewModel.HasError);
    }

    [Theory]
    [InlineData(LookControl.Brightness)]
    [InlineData(LookControl.Contrast)]
    [InlineData(LookControl.SharpenAmount)]
    [InlineData(LookControl.NormalStrength)]
    [InlineData(LookControl.SpecularScale)]
    public void ResetControl_AfterPreset_RestoresThatControlOnly(LookControl control)
    {
        // Arrange
        var look = new LookPanelViewModel();
        look.Load(new AetheriumForge.Core.Rules.LookSettings
        {
            Brightness = 0.4, Contrast = 1.5, Sharpen = new AetheriumForge.Core.Rules.SharpenSettings(1.0, 2.0), NormalStrength = 2.0, SpecularScale = 0.5,
        });

        // Act
        look.ResetControl(control);

        // Assert
        var settings = look.ToSettings();
        var values = new Dictionary<LookControl, double>
        {
            [LookControl.Brightness] = settings.Brightness,
            [LookControl.Contrast] = settings.Contrast,
            [LookControl.SharpenAmount] = settings.Sharpen.Amount,
            [LookControl.NormalStrength] = settings.NormalStrength,
            [LookControl.SpecularScale] = settings.SpecularScale,
        };
        Assert.Equal(1, values.Count(pair => pair.Value == LookControls.RangeOf(pair.Key).Neutral));
        Assert.Equal(LookControls.RangeOf(control).Neutral, values[control]);
    }
}
