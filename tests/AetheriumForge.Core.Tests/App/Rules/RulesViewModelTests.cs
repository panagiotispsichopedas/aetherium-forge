using AetheriumForge.App.Pages.Rules;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Tests.App.Rules;

public sealed class RulesViewModelTests : IAsyncLifetime
{
    private const string ClutterPath = @"textures\clutter\bucket01.dds";
    private const string RockPath = @"textures\landscape\rock01.dds";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly FakeNavigation _navigation = new();
    private readonly FakeRulesPageServices _services = new();
    private readonly FakeForgeRunner _runner = new();
    private JobStore _store = null!;
    private FakeProjectSession _session = null!;
    private RulesViewModel _vm = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _store = await JobStore.OpenAsync(Path.Combine(_folder, "forge.db"), Ct);
        await _store.UpsertJobsAsync(
            [
                Job(ClutterPath, "Clutter", 2048),
                Job(RockPath, "Large", 2048),
            ],
            Ct);
        await CreateViewModelAsync(ForgeRules.Default);
    }

    public async ValueTask DisposeAsync()
    {
        _vm.Dispose();
        await _store.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private static JobRecord Job(string path, string group, int target) =>
        new(0, path, "a.bsa", 512, 512, "BC1_UNORM", "Diffuse", group, target, "realesrgan-x4plus", JobState.Queued, 0, null, null, null);

    private async Task CreateViewModelAsync(ForgeRules projectRules)
    {
        _vm?.Dispose();
        _session = new FakeProjectSession(new ProjectInfo(_folder, "game", "data", "out", "work", projectRules), _store);
        _vm = new RulesViewModel(_session, _navigation, _services, _time, new SingleServiceProvider(_runner), NullLogger<RulesViewModel>.Instance);
        await _vm.LoadAsync();
        await SettleAsync();
    }

    private async Task SettleAsync()
    {
        _time.Advance(RulesViewModel.RecomputeDelay);
        await _vm.PendingRecompute;
    }

    private async Task<JobRecord> JobAsync(string path)
    {
        await foreach (var job in _store.GetJobsAsync(null, Ct))
        {
            if (job.Path == path)
            {
                return job;
            }
        }

        throw new InvalidOperationException(path);
    }

    private RuleGroupViewModel Group(string name) => _vm.Groups.Single(g => g.Name == name);

    [Fact]
    public void LoadAsync_DefaultRules_CountsEachGroup()
    {
        // Assert
        Assert.Equal([1, 1], _vm.Groups.Select(g => g.Count));
    }

    [Fact]
    public async Task Recompute_BeforeDebounceElapses_KeepsPreviousPreview()
    {
        // Arrange
        var before = _vm.Preview;

        // Act
        Group("Clutter").PatternsText = @"*\nothing\*";
        _time.Advance(RulesViewModel.RecomputeDelay - TimeSpan.FromMilliseconds(1));
        await Task.Yield();

        // Assert
        Assert.Same(before, _vm.Preview);
    }

    [Fact]
    public async Task Recompute_AfterDebounce_UpdatesCounts()
    {
        // Act
        Group("Clutter").PatternsText = @"*\nothing\*";
        await SettleAsync();

        // Assert
        Assert.Equal([0, 2], _vm.Groups.Select(g => g.Count));
    }

    [Fact]
    public async Task Recompute_EditDuringDebounce_RestartsTheDelay()
    {
        // Arrange
        Group("Clutter").PatternsText = @"*\nothing\*";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var before = _vm.Preview;

        // Act: a second edit 200 ms later; 100 ms more is not enough for it.
        Group("Clutter").CapText = "1024";
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Yield();

        // Assert
        Assert.Same(before, _vm.Preview);
    }

    [Fact]
    public async Task MoveUp_CatchAllGroup_TakesEveryMatch()
    {
        // Arrange
        _vm.SelectedGroup = Group("Large");

        // Act
        _vm.MoveUpCommand.Execute(null);
        await SettleAsync();

        // Assert
        Assert.Equal(("Large", 2, 0), (_vm.Groups[0].Name, _vm.Groups[0].Count, _vm.Groups[1].Count));
    }

    [Fact]
    public void MoveUp_FirstGroup_CannotExecute()
    {
        // Arrange
        _vm.SelectedGroup = _vm.Groups[0];

        // Act
        var canExecute = _vm.MoveUpCommand.CanExecute(null);

        // Assert
        Assert.False(canExecute);
    }

    [Fact]
    public void MoveDown_FirstGroup_BecomesSecond()
    {
        // Arrange
        _vm.SelectedGroup = Group("Clutter");

        // Act
        _vm.MoveDownCommand.Execute(null);

        // Assert
        Assert.Equal(["Large", "Clutter"], _vm.Groups.Select(g => g.Name));
    }

    [Fact]
    public void AddGroup_WithSelection_InsertsAfterIt()
    {
        // Arrange
        _vm.SelectedGroup = Group("Clutter");

        // Act
        _vm.AddGroupCommand.Execute(null);

        // Assert
        Assert.Equal(["Clutter", "New group", "Large"], _vm.Groups.Select(g => g.Name));
    }

    [Fact]
    public void DuplicateGroup_Selected_CopiesWithUniqueName()
    {
        // Arrange
        _vm.SelectedGroup = Group("Clutter");

        // Act
        _vm.DuplicateGroupCommand.Execute(null);

        // Assert
        Assert.Equal((@"*\clutter\*", 2048), (_vm.Groups[1].PatternsText, int.Parse(_vm.Groups[1].CapText, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task DeleteGroup_Confirmed_RemovesIt()
    {
        // Arrange
        _vm.SelectedGroup = Group("Clutter");
        _services.ConfirmAnswer = true;

        // Act
        await _vm.DeleteGroupCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["Large"], _vm.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task DeleteGroup_Declined_KeepsGroup()
    {
        // Arrange
        _vm.SelectedGroup = Group("Clutter");
        _services.ConfirmAnswer = false;

        // Act
        await _vm.DeleteGroupCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["Clutter", "Large"], _vm.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task Validation_AllGroupsDeleted_ShowsError()
    {
        // Arrange
        _services.ConfirmAnswer = true;

        // Act
        await _vm.DeleteGroupCommand.ExecuteAsync(null);
        await _vm.DeleteGroupCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("At least one group is required; with none, every texture is skipped.", _vm.ValidationError);
    }

    [Fact]
    public void Validation_DuplicateNameDifferentCase_ShowsError()
    {
        // Act
        Group("Large").Name = "CLUTTER";

        // Assert
        Assert.Equal("Group names must be unique: 'Clutter' is used more than once.", _vm.ValidationError);
    }

    [Fact]
    public async Task ApplyAsync_ManyRowsNewlySkippedAndDeclined_ChangesNothing()
    {
        // Arrange: excluding the landscape folder skips 1 of 2 rows (50 % > 10 %).
        _vm.ExclusionsText += $"{Environment.NewLine}textures\\landscape\\";
        _services.ConfirmAnswer = false;

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((JobState.Queued, (string?)null), ((await JobAsync(RockPath)).State, await _store.GetProjectValueAsync(RulesApplier.RulesProjectKey, Ct)));
    }

    [Fact]
    public async Task ApplyAsync_ManyRowsNewlySkipped_AsksWithCount()
    {
        // Arrange
        _vm.ExclusionsText += $"{Environment.NewLine}textures\\landscape\\";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["1 textures would be skipped. Apply anyway?"], _services.Questions);
    }

    [Fact]
    public async Task ApplyAsync_RunStartsDuringConfirmation_ChangesNothing()
    {
        // Arrange
        var job = await JobAsync(ClutterPath);
        await _store.MarkDoneAsync(job.Id, 2.0, Ct);
        _services.ConfirmAnswer = true;
        _services.WhileConfirming = () => _runner.SetState(PipelineState.Running);
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((JobState.Done, 2048), ((await JobAsync(ClutterPath)).State, (await JobAsync(ClutterPath)).Target));
    }

    [Fact]
    public async Task ApplyAsync_NormalStrengthChanged_AsksAboutFinishedRows()
    {
        // Arrange
        var job = await JobAsync(ClutterPath);
        await _store.MarkDoneAsync(job.Id, 2.0, Ct);
        Group("Clutter").NormalStrengthText = "2";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["1 finished textures would change. Re-queue them?"], _services.Questions);
    }

    [Fact]
    public async Task ExportAsync_WriteFails_ShowsError()
    {
        // Arrange
        _services.SaveException = new IOException("disk full");

        // Act
        await _vm.ExportCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Could not write the file: disk full", _vm.StatusMessage);
    }

    [Fact]
    public async Task ImportAsync_ReadFails_ShowsError()
    {
        // Arrange
        _services.OpenException = new UnauthorizedAccessException("denied");

        // Act
        await _vm.ImportCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Could not read the file: denied", _vm.StatusMessage);
    }

    [Fact]
    public async Task JobsChanged_RunActive_CoalescesReloadToOncePerSecond()
    {
        // Arrange
        _navigation.NavigateTo(AppPage.Rules);
        await WaitForAsync(() => !_vm.IsBusy);
        _runner.SetState(PipelineState.Running);
        await _store.UpsertJobsAsync([Job(@"textures\landscape\rock02.dds", "Large", 2048)], Ct);

        var recountBefore = _vm.PendingRecompute;

        // Act: three changes within the coalescing window give one reload, after the window.
        _session.NotifyJobsChanged();
        _session.NotifyJobsChanged();
        _session.NotifyJobsChanged();
        _time.Advance(RulesViewModel.ReloadCoalesceDelay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        bool reloadedEarly = !ReferenceEquals(recountBefore, _vm.PendingRecompute);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => !ReferenceEquals(recountBefore, _vm.PendingRecompute));
        await SettleAsync();

        // Assert
        Assert.Equal((false, 3), (reloadedEarly, _vm.Preview!.TotalJobs));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the view model.");
    }

    [Fact]
    public void Validation_NonNumericCap_ShowsInlineErrorAndDisablesApply()
    {
        // Act
        Group("Clutter").CapText = "abc";

        // Assert
        Assert.Equal((true, false), (Group("Clutter").HasError, _vm.ApplyCommand.CanExecute(null)));
    }

    [Fact]
    public void Validation_ZeroCap_UsesForgeRulesMessage()
    {
        // Act
        Group("Clutter").CapText = "0";

        // Assert
        Assert.Equal("Group 'Clutter': 'cap' must be positive.", _vm.ValidationError);
    }

    [Fact]
    public void Validation_MinAboveMax_ShowsError()
    {
        // Act
        Group("Large").MinSourceMaxText = "1024";
        Group("Large").MaxSourceMaxText = "512";

        // Assert
        Assert.Equal("Group 'Large': 'minSourceMax' must not exceed 'maxSourceMax'.", Group("Large").Error);
    }

    [Fact]
    public void Validation_NegativeMinSource_ShowsError()
    {
        // Act
        _vm.MinSourceText = "-1";

        // Assert
        Assert.Equal("'minSource' must not be negative.", _vm.ValidationError);
    }

    [Fact]
    public async Task ApplyAsync_QueuedRowsChange_UpdatesJobs()
    {
        // Arrange
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(1024, (await JobAsync(ClutterPath)).Target);
    }

    [Fact]
    public async Task ApplyAsync_Edited_StoresRulesInProject()
    {
        // Arrange
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        var stored = ForgeRules.FromJson((await _store.GetProjectValueAsync(RulesApplier.RulesProjectKey, Ct))!);
        Assert.Equal(1024, stored.Groups[0].Cap);
    }

    [Fact]
    public async Task ApplyAsync_Applied_NotifiesJobsChangedAndClearsDirty()
    {
        // Arrange
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((1, false), (_session.JobsChangedCount, _vm.IsDirty));
    }

    [Fact]
    public async Task ApplyAsync_RowNowExcluded_BecomesSkippedWithReason()
    {
        // Arrange
        _vm.ExclusionsText += $"{Environment.NewLine}textures\\landscape\\";
        _services.ConfirmAnswer = true;

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        var job = await JobAsync(RockPath);
        Assert.Equal((JobState.Skipped, "ExcludedFolder"), (job.State, job.Error));
    }

    [Fact]
    public async Task ApplyAsync_NoDoneRowsChange_DoesNotAsk()
    {
        // Arrange
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(_services.Questions);
    }

    [Fact]
    public async Task ApplyAsync_DoneRowWouldChange_AsksWithCount()
    {
        // Arrange
        var job = await JobAsync(ClutterPath);
        await _store.MarkDoneAsync(job.Id, 2.0, Ct);
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["1 finished textures would change. Re-queue them?"], _services.Questions);
    }

    [Fact]
    public async Task ApplyAsync_DoneRowAnsweredNo_KeepsItDone()
    {
        // Arrange
        var job = await JobAsync(ClutterPath);
        await _store.MarkDoneAsync(job.Id, 2.0, Ct);
        _services.ConfirmAnswer = false;
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        var after = await JobAsync(ClutterPath);
        Assert.Equal((JobState.Done, 2048), (after.State, after.Target));
    }

    [Fact]
    public async Task ApplyAsync_DoneRowAnsweredYes_RequeuesIt()
    {
        // Arrange
        var job = await JobAsync(ClutterPath);
        await _store.MarkDoneAsync(job.Id, 2.0, Ct);
        _services.ConfirmAnswer = true;
        Group("Clutter").CapText = "1024";

        // Act
        await _vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        var after = await JobAsync(ClutterPath);
        Assert.Equal((JobState.Queued, 1024), (after.State, after.Target));
    }

    [Fact]
    public void ApplyCommand_RunActive_CannotExecute()
    {
        // Act
        _runner.SetState(PipelineState.Running);

        // Assert
        Assert.False(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ApplyCommand_RunEnded_CanExecuteAgain()
    {
        // Arrange
        _runner.SetState(PipelineState.Running);

        // Act
        _runner.SetState(PipelineState.Idle);

        // Assert
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportThenImport_EditedRules_RoundTrips()
    {
        // Arrange
        Group("Clutter").CapText = "1024";
        Group("Large").Kinds[0].IsSelected = true;
        _vm.MinSourceText = "256";
        var exported = _vm.BuildRules(out _);
        await _vm.ExportCommand.ExecuteAsync(null);
        _vm.RevertCommand.Execute(null);
        _services.JsonToOpen = _services.SavedJson;

        // Act
        await _vm.ImportCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(exported, _vm.BuildRules(out _));
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_KeepsGroupsAndReports()
    {
        // Arrange
        _services.JsonToOpen = """{ "groups": [ { "name": "X", "match": ["*"], "cap": 0, "model": "m" } ] }""";

        // Act
        await _vm.ImportCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((2, true), (_vm.Groups.Count, _vm.StatusMessage!.StartsWith("The file is not valid rules JSON", StringComparison.Ordinal)));
    }

    [Fact]
    public void Revert_AfterEdit_RestoresProjectRules()
    {
        // Arrange
        Group("Clutter").CapText = "1024";

        // Act
        _vm.RevertCommand.Execute(null);

        // Assert
        Assert.Equal((ForgeRules.Default, false), (_vm.BuildRules(out _), _vm.IsDirty));
    }

    [Fact]
    public async Task ResetToDefaults_Confirmed_LoadsDefaults()
    {
        // Arrange
        _services.ConfirmAnswer = true;
        await _vm.DeleteGroupCommand.ExecuteAsync(null);

        // Act
        await _vm.ResetToDefaultsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(ForgeRules.Default, _vm.BuildRules(out _));
    }

    [Fact]
    public async Task ResetToDefaults_Declined_KeepsEdits()
    {
        // Arrange
        _services.Answers.Enqueue(true);
        _services.Answers.Enqueue(false);
        await _vm.DeleteGroupCommand.ExecuteAsync(null);

        // Act
        await _vm.ResetToDefaultsCommand.ExecuteAsync(null);

        // Assert
        Assert.Single(_vm.Groups);
    }

    [Fact]
    public async Task BuildRules_GroupWithLook_PreservesLookUntouched()
    {
        // Arrange
        var look = new LookSettings { Saturation = 1.2, Sharpen = new SharpenSettings(0.5, 1.0) };
        var rules = ForgeRules.Default with { Groups = [ForgeRules.Default.Groups[0] with { Look = look }, ForgeRules.Default.Groups[1]] };
        await CreateViewModelAsync(rules);

        // Act
        Group("Clutter").CapText = "1024";

        // Assert
        Assert.Same(look, _vm.BuildRules(out _)!.Groups[0].Look);
    }

    [Fact]
    public void LookSummary_NeutralGroup_SaysNeutral()
    {
        // Assert
        Assert.Equal("Neutral", Group("Clutter").LookSummary);
    }

    [Fact]
    public void DescribeLook_ChangedControls_ListsThem()
    {
        // Act
        var summary = RuleGroupViewModel.DescribeLook(new LookSettings { Saturation = 1.2, Contrast = 1.1 });

        // Assert
        Assert.Equal("Contrast 1.1, Saturation 1.2", summary);
    }

    [Fact]
    public void OpenCompare_Executed_NavigatesToCompare()
    {
        // Act
        _vm.OpenCompareCommand.Execute(null);

        // Assert
        Assert.Equal(AppPage.Compare, _navigation.Current);
    }

    [Fact]
    public async Task LoadAsync_StoredRules_WinOverSessionRules()
    {
        // Arrange
        var stored = ForgeRules.Default with { MinSource = 64 };
        await _store.SetProjectValueAsync(RulesApplier.RulesProjectKey, stored.ToJson(), Ct);

        // Act
        await _vm.LoadAsync();

        // Assert
        Assert.Equal("64", _vm.MinSourceText);
    }

    [Fact]
    public async Task LoadAsync_Timings_EstimatesTime()
    {
        // Arrange: 512×512 = 0.262144 MP per row, one row per group, 10 s/MP.
        _services.Timings = new Dictionary<string, double> { ["realesrgan-x4plus"] = 10.0 };

        // Act
        await _vm.LoadAsync();
        await SettleAsync();

        // Assert
        Assert.Equal(2 * 0.262144 * 10.0, _vm.Preview!.EstimatedSeconds!.Value, 6);
    }

    [Fact]
    public async Task ResetToDefaults_RowsWouldChangeGroup_ConfirmationShowsCount()
    {
        // Arrange: rename the catch-all, so its row would change group back to "Large".
        var rules = ForgeRules.Default with { Groups = [ForgeRules.Default.Groups[0], ForgeRules.Default.Groups[1] with { Name = "Everything" }] };
        var rock = await JobAsync(RockPath);
        await _store.ApplyRuleChangesAsync([new JobRuleChange(rock.Id, rock.Path, JobState.Queued, JobState.Queued, "Everything", 2048, "realesrgan-x4plus", null)], Ct);
        await CreateViewModelAsync(rules);

        // Act
        await _vm.ResetToDefaultsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(["Replace the groups, exclusions and minimum size with the defaults? 1 of 2 textures would change group. Nothing is saved until you apply."], _services.Questions);
    }
}
