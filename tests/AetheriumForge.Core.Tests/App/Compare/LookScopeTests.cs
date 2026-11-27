using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class LookScopeTests : IDisposable
{
    private readonly CompareFakeData _data = new();
    private readonly CompareFakeDialogs _dialogs = new();
    private readonly PreviewEngine _engine;
    private int _renders;

    public LookScopeTests() =>
        _engine = new PreviewEngine(
            new CompareFakeBackend(),
            () => PipelineState.Idle,
            PreviewMemoryCache.DefaultCapacityBytes,
            (input, look) =>
            {
                Interlocked.Increment(ref _renders);
                return LookRenderer.Render(input, look);
            },
            null);

    public void Dispose() => _engine.Dispose();

    private int Renders => Volatile.Read(ref _renders);

    private static LookPanelViewModel Layered()
    {
        var look = new LookPanelViewModel();
        look.LoadLayer(LookScope.All, LookOverrides.Empty.With(LookControl.Brightness, 0.1).With(LookControl.Contrast, 1.2));
        look.LoadLayer(LookScope.Group, LookOverrides.Empty.With(LookControl.Brightness, 0.3));
        look.LoadLayer(LookScope.Texture, LookOverrides.Empty.With(LookControl.Saturation, 1.5));
        return look;
    }

    private async Task<CompareViewModel> LoadedAsync(int jobs = 1)
    {
        for (var id = 1; id <= jobs; id++)
        {
            _data.Jobs.Add(CompareFakeData.Job(id, "Clutter"));
        }

        var viewModel = new CompareViewModel(_engine, _data, _dialogs, new Random(5));
        await viewModel.RefreshAsync();
        await viewModel.PreviewRandomAsync();
        await WaitUntilAsync(() => viewModel.AfterImage is not null && !viewModel.IsRenderPending);
        return viewModel;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the view model.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ── Scope logic ──

    [Fact]
    public void Resolve_AllGroupTexture_TextureWinsThenGroupThenAll()
    {
        // Arrange
        var look = Layered();

        // Act
        var effective = look.ToSettings();

        // Assert
        Assert.Equal((0.3, 1.2, 1.5, 1.0), (effective.Brightness, effective.Contrast, effective.Saturation, effective.Gamma));
    }

    [Fact]
    public void Row_TextureScopeUnsetControl_ShowsInheritedFromGroup()
    {
        // Arrange
        var look = Layered();

        // Act
        look.Scope = LookScope.Texture;

        // Assert
        var row = look[LookControl.Brightness];
        Assert.Equal((false, 0.3, "inherited from Group"), (row.IsSetHere, row.Value, row.InheritedFrom));
    }

    [Fact]
    public void Row_GroupScopeControlSetOnlyAtAll_ShowsInheritedFromAll()
    {
        // Arrange
        var look = Layered();

        // Act
        look.Scope = LookScope.Group;

        // Assert
        var row = look[LookControl.Contrast];
        Assert.Equal((1.2, "inherited from All"), (row.Value, row.InheritedFrom));
    }

    [Fact]
    public void Row_AllScopeUnsetControl_ShowsNeutralDefault()
    {
        // Arrange
        var look = Layered();

        // Act
        look.Scope = LookScope.All;

        // Assert
        var row = look[LookControl.Gamma];
        Assert.Equal((1.0, "default"), (row.Value, row.InheritedFrom));
    }

    [Fact]
    public void Row_ControlSetHere_KeepsInheritedValueForTheMarker()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.Group;

        // Act
        var row = look[LookControl.Brightness];

        // Assert
        Assert.Equal((true, 0.3, 0.1), (row.IsSetHere, row.Value, row.InheritedValue));
    }

    [Fact]
    public void SetValue_GroupScope_SetsOnlyTheGroupLayerAndMarksItDirty()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.Group;

        // Act
        look[LookControl.Gamma].Value = 1.4;

        // Assert
        Assert.Equal((true, false, true), (look.Working(LookScope.Group).IsSet(LookControl.Gamma), look.Working(LookScope.All).IsSet(LookControl.Gamma), look.IsScopeDirty));
    }

    [Fact]
    public void Scope_SwitchAfterEdit_KeepsEachScopesWorkingValues()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.Group;
        look.SetValue(LookControl.Gamma, 1.4);

        // Act
        look.Scope = LookScope.All;
        look.Scope = LookScope.Group;

        // Assert
        Assert.Equal(1.4, look[LookControl.Gamma].Value);
    }

    // ── Reset ──

    [Fact]
    public void ResetCommand_TextureScope_FallsBackToTheGroupValue()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.Texture;
        look.SetValue(LookControl.Brightness, -0.5);

        // Act
        look[LookControl.Brightness].ResetCommand.Execute(null);

        // Assert
        Assert.Equal((false, 0.3), (look[LookControl.Brightness].IsSetHere, look.ToSettings().Brightness));
    }

    [Fact]
    public void ResetCommand_AllScope_FallsBackToNeutral()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.All;

        // Act
        look[LookControl.Contrast].ResetCommand.Execute(null);

        // Assert
        Assert.Equal(1.0, look[LookControl.Contrast].Value);
    }

    [Fact]
    public void ResetCommand_ControlNotSetHere_CannotExecute()
    {
        // Arrange
        var look = Layered();
        look.Scope = LookScope.Group;

        // Act
        var canReset = look[LookControl.Gamma].ResetCommand.CanExecute(null);

        // Assert
        Assert.False(canReset);
    }

    [Fact]
    public void ResetName_Brightness_IsAccessibleLabel()
    {
        // Arrange
        var look = new LookPanelViewModel();

        // Act
        var name = look[LookControl.Brightness].ResetName;

        // Assert
        Assert.Equal("Reset Brightness", name);
    }

    // ── Render pending ──

    [Fact]
    public async Task SliderChange_SampleLoaded_MarksOutOfDateWithoutRendering()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        var rendersBefore = Renders;

        // Act
        viewModel.Look[LookControl.Brightness].Value = 0.4;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((true, rendersBefore, true), (viewModel.IsRenderPending, Renders, viewModel.RenderCommand.CanExecute(null)));
    }

    [Fact]
    public async Task RenderAsync_Pending_RendersOnceAndClearsOutOfDate()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);
        var rendersBefore = Renders;

        // Act
        await viewModel.RenderAsync();

        // Assert
        Assert.Equal((false, rendersBefore + 1), (viewModel.IsRenderPending, Renders));
    }

    [Fact]
    public async Task RenderCommand_NothingChanged_CannotExecute()
    {
        // Arrange
        var viewModel = await LoadedAsync();

        // Act
        var canRender = viewModel.RenderCommand.CanExecute(null);

        // Assert
        Assert.False(canRender);
    }

    [Fact]
    public async Task SliderBackToRenderedValue_ClearsOutOfDate()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);

        // Act
        viewModel.Look.ResetControl(LookControl.Brightness);

        // Assert
        Assert.False(viewModel.IsRenderPending);
    }

    // ── Undo ──

    [Fact]
    public async Task Undo_AfterRender_RestoresPreviousValuesAndRenderWithoutRendering()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        var firstImage = viewModel.AfterImage!.Pixels;
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);
        await viewModel.RenderAsync();
        var rendersBefore = Renders;

        // Act
        viewModel.Undo();

        // Assert
        Assert.Equal((false, rendersBefore, false), (viewModel.Look[LookControl.Brightness].IsSetHere, Renders, viewModel.IsRenderPending));
        Assert.Equal(firstImage, viewModel.AfterImage!.Pixels);
    }

    [Fact]
    public async Task Undo_TwoRenders_StepsBackOneRenderAtATime()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.SetValue(LookControl.Brightness, 0.2);
        await viewModel.RenderAsync();
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);
        await viewModel.RenderAsync();

        // Act
        viewModel.Undo();

        // Assert
        Assert.Equal((0.2, 1), (viewModel.Look[LookControl.Brightness].Value, viewModel.UndoDepth));
    }

    [Fact]
    public async Task UndoCommand_NoRenderYet_IsDisabled()
    {
        // Arrange
        var viewModel = await LoadedAsync();

        // Act
        var canUndo = viewModel.UndoCommand.CanExecute(null);

        // Assert
        Assert.False(canUndo);
    }

    [Fact]
    public async Task Scope_Changed_ClearsUndoStack()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);
        await viewModel.RenderAsync();

        // Act
        viewModel.Look.Scope = LookScope.All;

        // Assert
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public async Task Sample_Changed_ClearsUndoStack()
    {
        // Arrange
        var viewModel = await LoadedAsync(jobs: 2);
        viewModel.Look.SetValue(LookControl.Brightness, 0.4);
        await viewModel.RenderAsync();

        // Act
        viewModel.SelectedStripSample = viewModel.Samples[1];
        await WaitUntilAsync(() => !viewModel.IsLoading && !viewModel.IsRenderPending);

        // Assert
        Assert.False(viewModel.CanUndo);
    }

    // ── Apply per scope ──

    [Fact]
    public async Task ApplyAsync_AllScope_StoresAllLookAndRequeuesEveryFinishedTexture()
    {
        // Arrange
        _data.Jobs.Add(CompareFakeData.Job(10, "Large"));
        var viewModel = await LoadedAsync();
        _dialogs.Answer = true;
        viewModel.Look.Scope = LookScope.All;
        viewModel.Look.SetValue(LookControl.Saturation, 1.3);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((1.3, 2), (_data.Rules.Look!.Saturation, _data.Requeued.Count));
    }

    [Fact]
    public async Task ApplyAsync_GroupScope_WritesTheGroupsLookInTheRules()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.Scope = LookScope.Group;
        viewModel.Look.SetValue(LookControl.Contrast, 1.3);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal(1.3, _data.Rules.Groups[0].Look!.Contrast);
    }

    [Fact]
    public async Task ApplyAsync_TextureScope_StoresOverrideForTheSelectedTextureOnly()
    {
        // Arrange
        var viewModel = await LoadedAsync(jobs: 2);
        _dialogs.Answer = true;
        var path = viewModel.SelectedSample!.Job.Path;
        viewModel.Look.Scope = LookScope.Texture;
        viewModel.Look.SetValue(LookControl.Gamma, 0.8);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((0.8, viewModel.SelectedSample.Job.Id, 1, (double?)null), (_data.TextureLooks[path].Gamma, _data.Requeued.Single(), _data.Requeued.Count, _data.Rules.Groups[0].Look?.Gamma));
    }

    [Fact]
    public async Task ApplyAsync_TextureScopeWithoutSample_ShowsErrorAndSavesNothing()
    {
        // Arrange
        var viewModel = new CompareViewModel(_engine, _data, _dialogs, new Random(5));
        await viewModel.RefreshAsync();
        viewModel.Look.Scope = LookScope.Texture;

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((true, 0), (viewModel.HasError, _data.TextureLooks.Count + _data.Saves));
    }

    [Fact]
    public async Task ApplyAsync_UserDeclines_SavesButKeepsFinishedTextures()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        _dialogs.Answer = false;
        viewModel.Look.SetValue(LookControl.Contrast, 1.3);

        // Act
        await viewModel.ApplyAsync();

        // Assert
        Assert.Equal((1, 0, false), (_dialogs.Questions.Count, _data.Requeued.Count, viewModel.Look.IsScopeDirty));
    }

    [Fact]
    public void ApplyToFinishedMessage_ManyTextures_ListsFirstNamesAndCountsTheRest()
    {
        // Arrange
        var jobs = Enumerable.Range(1, 12).Select(id => CompareFakeData.Job(id, "Clutter")).ToList();

        // Act
        var message = CompareViewModel.ApplyToFinishedMessage(jobs);

        // Assert
        Assert.Equal((true, true, true), (message.StartsWith("Apply to 12 finished textures?", StringComparison.Ordinal), message.Contains(jobs[7].Path, StringComparison.Ordinal), message.Contains("…and 4 more", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Revert_AfterEdit_RestoresSavedValues()
    {
        // Arrange
        var viewModel = await LoadedAsync();
        viewModel.Look.SetValue(LookControl.Contrast, 1.3);

        // Act
        viewModel.Revert();

        // Assert
        Assert.Equal((false, 1.0), (viewModel.Look.IsScopeDirty, viewModel.Look[LookControl.Contrast].Value));
    }

    // ── Core store ──

    [Fact]
    public async Task Store_TextureOverride_RoundTrips()
    {
        // Arrange
        var store = new CoreLookScopeStore(_data);
        var look = LookOverrides.Empty.With(LookControl.Temperature, -0.25);
        await store.SaveAsync(LookScope.Texture, null, "textures\\clutter\\a.dds", look, TestContext.Current.CancellationToken);

        // Act
        var read = await store.GetAsync(LookScope.Texture, null, "textures\\clutter\\a.dds", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(look, read);
    }

    [Fact]
    public async Task Store_GroupWithoutLook_ReadsTheNormalStrengthAlias()
    {
        // Arrange
        var store = new CoreLookScopeStore(_data);

        // Act
        var read = await store.GetAsync(LookScope.Group, "Clutter", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1.4, read.NormalStrength);
    }

    [Fact]
    public async Task Store_AllScope_WritesForgeRulesLook()
    {
        // Arrange
        var store = new CoreLookScopeStore(_data);

        // Act
        await store.SaveAsync(LookScope.All, null, null, LookOverrides.Empty.With(LookControl.Gamma, 1.1), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1.1, _data.Rules.Look!.Gamma);
    }

    [Fact]
    public async Task FindAffectedFinishedAsync_ChangeThatDoesNotApplyToTheKind_ListsNothing()
    {
        // Arrange: a colour-only change does not change the look hash of a normal map.
        _data.Jobs.Add(CompareFakeData.Job(1, "Clutter", kind: "Normal"));
        var store = new CoreLookScopeStore(_data);
        await store.SaveAsync(LookScope.Group, "Clutter", null, LookOverrides.Empty.With(LookControl.Saturation, 1.5), TestContext.Current.CancellationToken);

        // Act
        var affected = await store.FindAffectedFinishedAsync(LookScope.Group, "Clutter", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(affected);
    }

    [Fact]
    public async Task Store_EmptyGroupLook_ClearsTheGroupsLook()
    {
        // Arrange
        var store = new CoreLookScopeStore(_data);
        await store.SaveAsync(LookScope.Group, "Clutter", null, LookOverrides.Empty.With(LookControl.Gamma, 1.2), TestContext.Current.CancellationToken);

        // Act
        await store.SaveAsync(LookScope.Group, "Clutter", null, LookOverrides.Empty, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(((LookOverrides?)null, 1.0), (_data.Rules.Groups[0].Look, _data.Rules.Groups[0].NormalStrength));
    }

    [Fact]
    public void Overrides_SameValuesDifferentOrder_AreEqual()
    {
        // Arrange
        var left = LookOverrides.Empty.With(LookControl.Gamma, 1.2).With(LookControl.Contrast, 0.9);
        var right = LookOverrides.Empty.With(LookControl.Contrast, 0.9).With(LookControl.Gamma, 1.2);

        // Act
        var equal = left.Equals(right) && left.GetHashCode() == right.GetHashCode();

        // Assert
        Assert.True(equal);
    }
}
