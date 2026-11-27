using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class CompareViewModelTests : IDisposable
{
    private readonly CompareFakeData _data = new();
    private readonly CompareFakeDialogs _dialogs = new();
    private readonly PreviewEngine _engine = new(new CompareFakeBackend(), () => PipelineState.Idle);

    public void Dispose() => _engine.Dispose();

    private CompareViewModel ViewModel(int seed = 42) => new(_engine, _data, _dialogs, new Random(seed));

    [Fact]
    public void ApplyPreset_Vivid_LoadsPresetIntoLookPanel()
    {
        // Arrange
        var viewModel = ViewModel();

        // Act
        viewModel.ApplyPreset(LookPresets.Vivid);

        // Assert
        Assert.Equal(LookPresets.Vivid.Look, viewModel.Look.ToSettings());
    }

    [Fact]
    public void ApplyPreset_Neutral_AfterVivid_RestoresNeutral()
    {
        // Arrange
        var viewModel = ViewModel();
        viewModel.ApplyPreset(LookPresets.Vivid);

        // Act
        viewModel.ApplyPreset(LookPresets.Neutral);

        // Assert
        Assert.True(viewModel.Look.ToSettings().IsNeutral);
    }

    [Fact]
    public void ApplyPreset_Any_RaisesLookChangedOnce()
    {
        // Arrange
        var look = new LookPanelViewModel();
        var raised = 0;
        look.LookChanged += (_, _) => raised++;

        // Act
        look.ApplyPreset(LookPresets.DeepRelief.Look);

        // Assert
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(TextureKind.Diffuse, true, true, false, false)]
    [InlineData(TextureKind.Glow, true, true, false, false)]
    [InlineData(TextureKind.Normal, false, false, true, true)]
    [InlineData(TextureKind.Specular, false, false, false, true)]
    [InlineData(TextureKind.EnvironmentMask, false, false, false, false)]
    public void LookPanel_Kind_EnablesOnlyApplicableControls(TextureKind kind, bool colour, bool sharpen, bool normal, bool specular)
    {
        // Arrange
        var look = new LookPanelViewModel();

        // Act
        look.Kind = kind;

        // Assert
        Assert.Equal((colour, sharpen, normal, specular), (look[LookControl.Brightness].IsApplicable, look[LookControl.SharpenAmount].IsApplicable, look[LookControl.NormalStrength].IsApplicable, look[LookControl.SpecularScale].IsApplicable));
    }

    [Fact]
    public void LookPanel_ParallaxKind_DisablesAiStrength()
    {
        // Arrange
        var look = new LookPanelViewModel();

        // Act
        look.Kind = TextureKind.Parallax;

        // Assert
        Assert.False(look[LookControl.AiStrength].IsApplicable);
    }

    [Fact]
    public void LookPanel_AnyKind_KeepsDenoiseDisabledWithTooltip()
    {
        // Arrange
        var look = new LookPanelViewModel { Kind = TextureKind.Diffuse };

        // Act
        var enabled = look.IsDenoiseEnabled;

        // Assert
        Assert.Equal((false, "needs a denoise model"), (enabled, look.DenoiseTooltip));
    }

    [Fact]
    public void LookPanel_KindChanged_RaisesRowApplicabilityChange()
    {
        // Arrange
        var look = new LookPanelViewModel();
        var changed = new List<string?>();
        look[LookControl.NormalStrength].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        look.Kind = TextureKind.Normal;

        // Assert
        Assert.Contains(nameof(LookControlViewModel.IsApplicable), changed);
    }

    [Fact]
    public async Task ApplyAsync_UserDeclines_SavesLookWithoutRequeue()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter"));
        _dialogs.Answer = false;
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        viewModel.ApplyPreset(LookPresets.Crisp);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((LookPresets.Crisp.Look, 1, 0), (LookResolver.Resolve(_data.Rules, "Clutter", null), _dialogs.Questions.Count, _data.Requeued.Count));
    }

    [Fact]
    public async Task ApplyAsync_UserConfirms_RequeuesTheGroupsDoneJobs()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter"));
        _data.Jobs.Add(CompareFakeData.Job(2, "Clutter", JobState.Queued));
        _data.Jobs.Add(CompareFakeData.Job(3, "Large"));
        _dialogs.Answer = true;
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        viewModel.ApplyPreset(LookPresets.Soft);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal([1L], _data.Requeued);
    }

    [Fact]
    public async Task ApplyAsync_Confirmation_AsksTheApplyToFinishedQuestion()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter"));
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        viewModel.Look.SetValue(LookControl.Contrast, 1.2);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.StartsWith(CompareViewModel.ApplyToFinishedQuestion, _dialogs.Questions.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_NoFinishedJobs_DoesNotAsk()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter", JobState.Queued));
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((1, 0), (_data.Saves, _dialogs.Questions.Count));
    }

    [Fact]
    public async Task ApplyAsync_NormalStrength_AlsoWritesGroupAlias()
    {
        // Arrange
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        viewModel.Look.SetValue(LookControl.NormalStrength, 2.0);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal(2.0, _data.Rules.Groups[0].NormalStrength);
    }

    [Fact]
    public async Task RefreshAsync_GroupWithoutLook_LoadsGroupNormalStrength()
    {
        // Arrange
        var viewModel = ViewModel();

        // Act
        await viewModel.RefreshAsync();

        // Assert
        Assert.Equal(1.4, viewModel.Look[LookControl.NormalStrength].Value);
    }

    [Fact]
    public async Task UseModelForGroupAsync_OtherModel_WritesGroupModel()
    {
        // Arrange
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedModel = "other";

        // Act
        await viewModel.UseModelForGroupAsync();

        // Assert
        Assert.Equal(("other", false), (_data.Rules.Groups[0].Model, viewModel.IsModelDifferentFromGroup));
    }

    [Fact]
    public void PickRandom_SameSeed_ReturnsSamePicks()
    {
        // Arrange
        var jobs = Enumerable.Range(1, 50).Select(id => CompareFakeData.Job(id, "Clutter")).ToList();

        // Act
        var first = CompareViewModel.PickRandom(jobs, 10, new Random(7)).Select(job => job.Id);
        var second = CompareViewModel.PickRandom(jobs, 10, new Random(7)).Select(job => job.Id);

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void PickRandom_FewerJobsThanCount_ReturnsEachJobOnce()
    {
        // Arrange
        var jobs = Enumerable.Range(1, 3).Select(id => CompareFakeData.Job(id, "Clutter")).ToList();

        // Act
        var picks = CompareViewModel.PickRandom(jobs, 10, new Random(1));

        // Assert
        Assert.Equal([1L, 2L, 3L], picks.Select(job => job.Id).Order());
    }

    [Fact]
    public async Task PreviewRandomAsync_InjectedSeed_FillsStripDeterministically()
    {
        // Arrange
        for (var id = 1; id <= 30; id++)
        {
            _data.Jobs.Add(CompareFakeData.Job(id, "Clutter", JobState.Queued));
        }

        var first = ViewModel(seed: 3);
        var second = ViewModel(seed: 3);
        await first.RefreshAsync();
        await second.RefreshAsync();

        // Act
        await first.PreviewRandomAsync();
        await second.PreviewRandomAsync();

        // Assert
        Assert.Equal(first.Samples.Select(sample => sample.Job.Id), second.Samples.Select(sample => sample.Job.Id));
    }

    [Fact]
    public async Task PreviewRandomAsync_DefaultCount_PicksTenSamples()
    {
        // Arrange
        for (var id = 1; id <= 30; id++)
        {
            _data.Jobs.Add(CompareFakeData.Job(id, "Clutter", JobState.Queued));
        }

        var viewModel = ViewModel();
        await viewModel.RefreshAsync();

        // Act
        await viewModel.PreviewRandomAsync();

        // Assert
        Assert.Equal(CompareViewModel.DefaultSampleCount, viewModel.Samples.Count);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(250, 250)]
    [InlineData(900, 400)]
    public void SetZoom_Value_ClampsTo100To400(int requested, int expected)
    {
        // Arrange
        var viewModel = ViewModel();

        // Act
        viewModel.SetZoom(requested);

        // Assert
        Assert.Equal((expected, false), (viewModel.Zoom, viewModel.IsFitToView));
    }

    [Fact]
    public void ZoomIn_FromFitBelowActualSize_GoesTo100Percent()
    {
        // Arrange
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight, viewModel.ViewportWidth, viewModel.ViewportHeight) = (2048, 2048, 800, 600);

        // Act
        viewModel.ZoomIn();

        // Assert
        Assert.Equal(100, viewModel.Zoom);
    }

    [Fact]
    public void ZoomOut_At100Percent_ReturnsToFit()
    {
        // Arrange
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight, viewModel.ViewportWidth, viewModel.ViewportHeight) = (2048, 2048, 800, 600);
        viewModel.SetZoom(100);

        // Act
        viewModel.ZoomOut();

        // Assert
        Assert.True(viewModel.IsFitToView);
    }

    [Fact]
    public void PanBy_BeyondImageEdge_ClampsToHalfTheOverflow()
    {
        // Arrange: 2048 at 200 % in an 800×600 viewport overflows by 3296 × 3496.
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight, viewModel.ViewportWidth, viewModel.ViewportHeight) = (2048, 2048, 800, 600);
        viewModel.SetZoom(200);

        // Act
        viewModel.PanBy(10_000, -10_000);

        // Assert
        Assert.Equal((1648.0, -1748.0), (viewModel.PanX, viewModel.PanY));
    }

    [Fact]
    public void PanBy_ImageSmallerThanViewport_StaysCentred()
    {
        // Arrange
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight, viewModel.ViewportWidth, viewModel.ViewportHeight) = (256, 256, 800, 600);
        viewModel.SetZoom(100);

        // Act
        viewModel.PanBy(50, 50);

        // Assert
        Assert.Equal((0.0, 0.0), (viewModel.PanX, viewModel.PanY));
    }

    [Fact]
    public void FitToView_AfterPan_ResetsPanAndFits()
    {
        // Arrange
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight, viewModel.ViewportWidth, viewModel.ViewportHeight) = (2048, 1024, 800, 600);
        viewModel.SetZoom(400);
        viewModel.PanBy(300, 300);

        // Act
        viewModel.FitToView();

        // Assert
        Assert.Equal((0.0, 0.0, 800.0), (viewModel.PanX, viewModel.PanY, viewModel.DisplayWidth));
    }

    [Fact]
    public void SetSplitFromOffset_PastTheEdge_ClampsToOne()
    {
        // Arrange
        var viewModel = ViewModel();
        (viewModel.TargetWidth, viewModel.TargetHeight) = (512, 512);
        viewModel.SetZoom(100);

        // Act
        viewModel.SetSplitFromOffset(900);

        // Assert
        Assert.Equal((1.0, 512.0), (viewModel.Split, viewModel.SplitOffset));
    }

    [Fact]
    public async Task SelectedStripSample_Picked_RendersAfterImageAtTargetSize()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter", JobState.Queued));
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        await viewModel.PreviewRandomAsync();

        // Act
        await WaitUntilAsync(() => viewModel.AfterImage is not null);

        // Assert
        Assert.Equal((16, 16, 8), (viewModel.AfterImage!.Width, viewModel.AfterImage.Height, viewModel.BeforeImage!.Width));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the view model.");
            await Task.Delay(10);
        }
    }
}
