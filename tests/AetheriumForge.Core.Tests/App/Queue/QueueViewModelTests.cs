using AetheriumForge.App.Pages.Queue;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Tests.App.Queue;

public sealed class QueueViewModelTests : IAsyncLifetime
{
    private const string QueuedPath = @"textures\clutter\bucket01.dds";
    private const string QueuedPath2 = @"textures\clutter\bucket02.dds";
    private const string DonePath = @"textures\armor\iron\cuirass.dds";
    private const string FailedPath = @"textures\armor\iron\boots.dds";
    private const string SkippedPath = @"textures\effects\glow.dds";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeProjectSession _session = new();
    private readonly FakeForgeRunner _runner = new();
    private readonly FakeNavigationService _navigation = new();
    private readonly ManualTimeProvider _time = new();
    private IReadOnlyList<ModelInfo> _models = [Model("model-a"), Model("model-b")];
    private JobStore _store = null!;
    private QueueViewModel? _viewModel;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = await JobStore.OpenAsync(Path.Combine(_root, "forge.db"), Ct);
        await _store.UpsertJobsAsync(
            [
                Job(QueuedPath, JobState.Queued, "clutter", "diffuse"),
                Job(QueuedPath2, JobState.Queued, "clutter", "normal"),
                Job(DonePath, JobState.Done, "armor", "diffuse"),
                Job(FailedPath, JobState.Failed, "armor", "diffuse"),
                Job(SkippedPath, JobState.Skipped, null, "glow"),
            ],
            Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_viewModel is not null)
        {
            await _viewModel.LoadTask;
            _viewModel.Dispose();
        }

        await _store.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static ModelInfo Model(string name) => new(name, $@"C:\models\{name}.onnx", name, 4, "fp32", null, []);

    private static JobRecord Job(string path, JobState state, string? group, string kind) =>
        new(0, path, "Skyrim - Textures0.bsa", 512, 512, "BC1_UNORM", kind, group, 2048, "model-a", state, 0, state == JobState.Failed ? "boom" : null, null, null);

    private async Task<QueueViewModel> OpenAsync(bool withRunner = true)
    {
        _session.Open(_root, _store);
        _viewModel = new QueueViewModel(_session, withRunner ? _runner : null, _navigation, _time, new InlineUiDispatcher(), () => _models, NullLogger<QueueViewModel>.Instance);
        await _viewModel.LoadTask;

        return _viewModel;
    }

    private static JobRowViewModel Row(QueueViewModel viewModel, string path) => viewModel.Rows.Single(row => row.Path == path);

    private async Task<JobRecord> StoredAsync(string path)
    {
        await foreach (var job in _store.QueryJobsAsync(new JobQuery { PathContains = path }, Ct))
        {
            return job;
        }

        throw new InvalidOperationException(path);
    }

    private static void Select(QueueViewModel viewModel, params string[] paths) =>
        viewModel.UpdateSelection(viewModel.Rows.Where(row => paths.Contains(row.Path)));

    private string OutputFile(string path) => Path.Combine(_root, "output", path);

    [Fact]
    public void Constructor_NoSession_ShowsNoProject()
    {
        // Act
        _viewModel = new QueueViewModel(null, null, _navigation, _time, new InlineUiDispatcher(), () => _models, NullLogger<QueueViewModel>.Instance);

        // Assert
        Assert.True(_viewModel.ShowNoProject);
    }

    [Fact]
    public async Task Constructor_OpenProject_LoadsEveryRow()
    {
        // Act
        var viewModel = await OpenAsync();

        // Assert
        Assert.Equal(5, viewModel.Rows.Count);
    }

    [Fact]
    public async Task Constructor_OpenProject_LoadsGroupAndKindFilterValues()
    {
        // Act
        var viewModel = await OpenAsync();

        // Assert
        Assert.Equal("(all),armor,clutter|(all),diffuse,glow,normal", $"{string.Join(',', viewModel.Groups)}|{string.Join(',', viewModel.Kinds)}");
    }

    [Fact]
    public async Task OpenScanCommand_Executed_NavigatesToScan()
    {
        // Arrange
        _viewModel = new QueueViewModel(null, null, _navigation, _time, new InlineUiDispatcher(), () => _models, NullLogger<QueueViewModel>.Instance);

        // Act
        _viewModel.OpenScanCommand.Execute(null);

        // Assert
        Assert.Equal(AppPage.Scan, _navigation.Current);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SessionChanged_ProjectClosed_ShowsNoProject()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        _session.Close();
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((true, 0), (viewModel.ShowNoProject, viewModel.Rows.Count));
    }

    [Fact]
    public async Task BuildQuery_AllStatesSelected_UsesNoStateFilter()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        var query = viewModel.BuildQuery();

        // Assert
        Assert.Null(query.States);
    }

    [Fact]
    public async Task BuildQuery_FiltersSet_MapsEveryFilter()
    {
        // Arrange
        var viewModel = await OpenAsync();
        viewModel.StateFilters.Single(option => option.State == JobState.Done).IsSelected = false;
        await viewModel.LoadTask;
        viewModel.SelectedGroup = "armor";
        await viewModel.LoadTask;
        viewModel.SelectedKind = "diffuse";
        await viewModel.LoadTask;
        viewModel.SearchText = "iron";

        // Act
        var query = viewModel.BuildQuery();

        // Assert
        Assert.Equal((4, "armor", "diffuse", "iron"), (query.States!.Count, query.Group, query.Kind, query.PathContains));
    }

    [Fact]
    public async Task StateFilter_Deselected_ReloadsWithoutThatState()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.StateFilters.Single(option => option.State == JobState.Queued).IsSelected = false;
        await viewModel.LoadTask;

        // Assert
        Assert.DoesNotContain(viewModel.Rows, row => row.State == JobState.Queued);
    }

    [Fact]
    public async Task StateFilter_GroupSelected_CountsFollowOtherFilters()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.SelectedGroup = "clutter";
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((2, 0), (viewModel.StateFilters.Single(o => o.State == JobState.Queued).Count, viewModel.StateFilters.Single(o => o.State == JobState.Done).Count));
    }

    [Fact]
    public async Task SelectedKind_Changed_ShowsOnlyThatKind()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.SelectedKind = "normal";
        await viewModel.LoadTask;

        // Assert
        Assert.Equal([QueuedPath2], viewModel.Rows.Select(row => row.Path));
    }

    [Fact]
    public async Task SearchText_BeforeDebounce_DoesNotReload()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;

        // Act
        viewModel.SearchText = "iron";
        _time.Advance(TimeSpan.FromMilliseconds(249));

        // Assert
        Assert.Equal(loads, viewModel.LoadCount);
    }

    [Fact]
    public async Task SearchText_AfterDebounce_ReloadsFiltered()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.SearchText = "iron";
        _time.Advance(QueueViewModel.SearchDebounce);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(2, viewModel.Rows.Count);
    }

    [Fact]
    public async Task SearchText_TypingContinues_RestartsDebounceAndLoadsOnce()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;

        // Act
        viewModel.SearchText = "i";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        viewModel.SearchText = "ir";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        viewModel.SearchText = "iron";
        _time.Advance(QueueViewModel.SearchDebounce);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(loads + 1, viewModel.LoadCount);
    }

    [Fact]
    public async Task SortBy_SameColumnTwice_TogglesToDescending()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(viewModel.Rows.Select(row => row.Path).OrderDescending(StringComparer.Ordinal), viewModel.Rows.Select(row => row.Path));
    }

    [Fact]
    public async Task SortBy_NewColumn_StartsAscending()
    {
        // Arrange
        var viewModel = await OpenAsync();
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;

        // Act
        viewModel.SortBy(JobSortColumn.State);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((JobSortColumn.State, false, JobState.Queued), (viewModel.SortColumn, viewModel.SortDescending, viewModel.Rows[0].State));
    }

    [Fact]
    public async Task Filter_NoMatches_ShowsNoRowsEmptyState()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        viewModel.SearchText = "does-not-exist";
        _time.Advance(QueueViewModel.SearchDebounce);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((true, false), (viewModel.ShowNoRows, viewModel.ShowGrid));
    }

    [Fact]
    public async Task ClearFiltersCommand_FiltersSet_ShowsEveryRow()
    {
        // Arrange
        var viewModel = await OpenAsync();
        viewModel.SelectedGroup = "armor";
        await viewModel.LoadTask;
        viewModel.StateFilters[0].IsSelected = false;
        await viewModel.LoadTask;

        // Act
        viewModel.ClearFiltersCommand.Execute(null);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(5, viewModel.Rows.Count);
    }

    [Fact]
    public async Task RetryCommand_MixedSelection_QueuesOnlyFailed()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, FailedPath, DonePath);

        // Act
        await viewModel.RetryCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((JobState.Queued, JobState.Done), ((await StoredAsync(FailedPath)).State, (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task RetryCommand_Executed_NotifiesJobsChanged()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, FailedPath);

        // Act
        await viewModel.RetryCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(1, _session.NotifyCount);
    }

    [Fact]
    public async Task RetryCommand_NoFailedSelected_CannotExecute()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        Select(viewModel, DonePath, QueuedPath);

        // Assert
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task SkipCommand_MixedSelection_SkipsQueuedAndFailedOnly()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, QueuedPath, FailedPath, DonePath);

        // Act
        await viewModel.SkipCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(
            (JobState.Skipped, JobState.Skipped, JobState.Done),
            ((await StoredAsync(QueuedPath)).State, (await StoredAsync(FailedPath)).State, (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task Actions_RunActive_CannotExecute()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, QueuedPath, FailedPath, DonePath);

        // Act
        _runner.SetState(PipelineState.Running);

        // Assert
        Assert.Equal(
            (false, false, false, false),
            (viewModel.RetryCommand.CanExecute(null), viewModel.SkipCommand.CanExecute(null), viewModel.RequeueCommand.CanExecute(null), viewModel.ChangeModelCommand.CanExecute(null)));
    }

    [Fact]
    public async Task Actions_RunEnded_CanExecuteAgain()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, FailedPath);
        _runner.SetState(PipelineState.Paused);

        // Act
        _runner.SetState(PipelineState.Idle);

        // Assert
        Assert.True(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Actions_NoRunnerRegistered_CanExecute()
    {
        // Arrange
        var viewModel = await OpenAsync(withRunner: false);

        // Act
        Select(viewModel, FailedPath);

        // Assert
        Assert.True(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RequeueCommand_BeforeConfirm_KeepsOutputAndState()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile(DonePath))!);
        await File.WriteAllTextAsync(OutputFile(DonePath), "dds", Ct);
        Select(viewModel, DonePath);

        // Act
        viewModel.RequeueCommand.Execute(null);

        // Assert
        Assert.Equal((true, true, JobState.Done), (viewModel.IsRequeueConfirmOpen, File.Exists(OutputFile(DonePath)), (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task CancelRequeueCommand_AfterPrompt_KeepsOutputAndState()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile(DonePath))!);
        await File.WriteAllTextAsync(OutputFile(DonePath), "dds", Ct);
        Select(viewModel, DonePath);
        viewModel.RequeueCommand.Execute(null);

        // Act
        viewModel.CancelRequeueCommand.Execute(null);

        // Assert
        Assert.Equal((false, true, JobState.Done), (viewModel.IsRequeueConfirmOpen, File.Exists(OutputFile(DonePath)), (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task ConfirmRequeueCommand_AfterPrompt_DeletesOutputAndQueues()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile(DonePath))!);
        await File.WriteAllTextAsync(OutputFile(DonePath), "dds", Ct);
        Select(viewModel, DonePath, QueuedPath);
        viewModel.RequeueCommand.Execute(null);

        // Act
        await viewModel.ConfirmRequeueCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((false, JobState.Queued), (File.Exists(OutputFile(DonePath)), (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task ConfirmRequeueCommand_PathOutsideOutputTextures_RejectsAndDeletesNothing()
    {
        // Arrange: a crafted row whose path escapes <OutputRoot>\textures.
        await _store.UpsertJobsAsync([Job(@"textures\..\..\victim.dds", JobState.Done, "armor", "diffuse")], Ct);
        var victim = Path.Combine(_root, "victim.dds");
        await File.WriteAllTextAsync(victim, "keep me", Ct);
        var viewModel = await OpenAsync();
        Select(viewModel, @"textures\..\..\victim.dds");
        viewModel.RequeueCommand.Execute(null);

        // Act
        await viewModel.ConfirmRequeueCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((true, JobState.Done), (File.Exists(victim), (await StoredAsync("victim")).State));
    }

    [Fact]
    public async Task ConfirmRequeueCommand_JobLeftDoneAfterPrompt_KeepsOutput()
    {
        // Arrange: the job is skipped elsewhere between the prompt and the confirmation (the page has not refreshed yet).
        var viewModel = await OpenAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile(DonePath))!);
        await File.WriteAllTextAsync(OutputFile(DonePath), "dds", Ct);
        Select(viewModel, DonePath);
        viewModel.RequeueCommand.Execute(null);
        await _store.SetStateAsync([Row(viewModel, DonePath).Id], JobState.Skipped, null, Ct);

        // Act
        await viewModel.ConfirmRequeueCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((true, JobState.Skipped), (File.Exists(OutputFile(DonePath)), (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task ConfirmRequeueCommand_RowRefreshedOutOfDone_KeepsOutput()
    {
        // Arrange: the job moved to Failed and the page refreshed before the confirmation.
        var viewModel = await OpenAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile(DonePath))!);
        await File.WriteAllTextAsync(OutputFile(DonePath), "dds", Ct);
        Select(viewModel, DonePath);
        viewModel.RequeueCommand.Execute(null);
        await _store.SetStateAsync([Row(viewModel, DonePath).Id], JobState.Failed, "later", Ct);
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;

        // Act
        await viewModel.ConfirmRequeueCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((true, JobState.Failed), (File.Exists(OutputFile(DonePath)), (await StoredAsync(DonePath)).State));
    }

    [Fact]
    public async Task ConfirmRequeueCommand_MissingOutputFile_StillQueues()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, DonePath);
        viewModel.RequeueCommand.Execute(null);

        // Act
        await viewModel.ConfirmRequeueCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(JobState.Queued, (await StoredAsync(DonePath)).State);
    }

    [Fact]
    public async Task ChangeModelCommand_ModelsInstalled_OpensPickerWithModels()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, QueuedPath);

        // Act
        await viewModel.ChangeModelCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((true, "model-a,model-b"), (viewModel.IsModelPickerOpen, string.Join(',', viewModel.AvailableModels)));
    }

    [Fact]
    public async Task ChangeModelCommand_ModelsFolderUnreadable_ReportsErrorAndKeepsPickerClosed()
    {
        // Arrange
        _session.Open(_root, _store);
        _viewModel = new QueueViewModel(
            _session, _runner, _navigation, _time, new InlineUiDispatcher(), () => throw new UnauthorizedAccessException("denied"), NullLogger<QueueViewModel>.Instance);
        await _viewModel.LoadTask;
        Select(_viewModel, QueuedPath);

        // Act
        await _viewModel.ChangeModelCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((false, "Change model failed: denied"), (_viewModel.IsModelPickerOpen, _viewModel.StatusMessage));
    }

    [Fact]
    public async Task LockReason_RunActive_ShowsLockedWhileForging()
    {
        // Arrange
        var viewModel = await OpenAsync();

        // Act
        _runner.SetState(PipelineState.Running);

        // Assert
        Assert.Equal((true, QueueViewModel.RunLockReason), (viewModel.IsLocked, viewModel.LockReason));
    }

    [Fact]
    public async Task LockReason_Idle_IsEmpty()
    {
        // Act
        var viewModel = await OpenAsync();

        // Assert
        Assert.Equal((false, string.Empty), (viewModel.IsLocked, viewModel.LockReason));
    }

    [Fact]
    public async Task ChangeModelCommand_NoModels_KeepsPickerClosed()
    {
        // Arrange
        _models = [];
        var viewModel = await OpenAsync();
        Select(viewModel, QueuedPath);

        // Act
        await viewModel.ChangeModelCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal((false, true), (viewModel.IsModelPickerOpen, viewModel.StatusMessage.StartsWith("No models", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyModelCommand_ModelPicked_SetsModelOnSelectedJobs()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, QueuedPath, DonePath);
        await viewModel.ChangeModelCommand.ExecuteAsync(null);
        viewModel.SelectedModel = "model-b";

        // Act
        await viewModel.ApplyModelCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(("model-b", "model-b", "model-a"), ((await StoredAsync(QueuedPath)).Model, (await StoredAsync(DonePath)).Model, (await StoredAsync(FailedPath)).Model));
    }

    [Fact]
    public async Task JobsChanged_ManyEventsBeforeInterval_DoesNotReload()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;

        // Act
        for (var i = 0; i < 50; i++)
        {
            _session.NotifyJobsChanged();
        }

        _time.Advance(QueueViewModel.RefreshInterval - TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(loads, viewModel.LoadCount);
    }

    [Fact]
    public async Task JobsChanged_ManyEventsInOneInterval_ReloadsOnce()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;

        // Act
        for (var i = 0; i < 50; i++)
        {
            _session.NotifyJobsChanged();
        }

        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;
        _time.Advance(QueueViewModel.RefreshInterval * 5);

        // Assert
        Assert.Equal(loads + 1, viewModel.LoadCount);
    }

    [Fact]
    public async Task JobsChanged_EventsAfterTick_ScheduleAnotherReload()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;

        // Act
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(loads + 2, viewModel.LoadCount);
    }

    [Fact]
    public async Task JobsChanged_RowStateChanged_UpdatesSameRowInstanceInPlace()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var rows = viewModel.Rows;
        var row = Row(viewModel, QueuedPath);
        await _store.MarkDoneAsync(row.Id, 2.5, Ct);

        // Act
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((true, true, JobState.Done, "2.5"), (ReferenceEquals(rows, viewModel.Rows), ReferenceEquals(row, Row(viewModel, QueuedPath)), row.State, row.Time));
    }

    [Fact]
    public async Task JobsChanged_RowLeavesStateFilter_RemovesItWithoutRebuilding()
    {
        // Arrange
        var viewModel = await OpenAsync();
        foreach (var option in viewModel.StateFilters.Where(option => option.State != JobState.Queued))
        {
            option.IsSelected = false;
            await viewModel.LoadTask;
        }

        var rows = viewModel.Rows;
        var survivor = Row(viewModel, QueuedPath2);
        await _store.MarkDoneAsync(Row(viewModel, QueuedPath).Id, 1, Ct);

        // Act
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((true, 1, true), (ReferenceEquals(rows, viewModel.Rows), viewModel.Rows.Count, ReferenceEquals(survivor, viewModel.Rows[0])));
    }

    [Fact]
    public async Task SortBy_SelectionPresent_RequestsSelectionRestore()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, DonePath);
        IReadOnlyList<JobRowViewModel>? restored = null;
        viewModel.SelectionRestoreRequested += (_, rows) => restored = rows;

        // Act
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal([DonePath], restored!.Select(row => row.Path));
    }

    [Fact]
    public async Task SortBy_GridClearsSelectionOnSwap_KeepsSelection()
    {
        // Arrange: like the DataGrid, report an empty selection whenever Rows is replaced.
        var viewModel = await OpenAsync();
        Select(viewModel, DonePath, FailedPath);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueViewModel.Rows))
            {
                viewModel.UpdateSelection([]);
            }
        };
        IReadOnlyList<JobRowViewModel>? restored = null;
        viewModel.SelectionRestoreRequested += (_, rows) => restored = rows;

        // Act
        viewModel.SortBy(JobSortColumn.Path);
        await viewModel.LoadTask;

        // Assert
        Assert.Equal((2, $"{FailedPath},{DonePath}"), (viewModel.SelectedCount, string.Join(',', restored!.Select(row => row.Path))));
    }

    [Fact]
    public async Task FilterChange_SelectedRowFilteredOut_DropsItFromSelection()
    {
        // Arrange
        var viewModel = await OpenAsync();
        Select(viewModel, DonePath, FailedPath);

        // Act
        viewModel.StateFilters.Single(option => option.State == JobState.Done).IsSelected = false;
        await viewModel.LoadTask;

        // Assert
        Assert.Equal(1, viewModel.SelectedCount);
    }

    [Fact]
    public async Task Dispose_AfterDispose_IgnoresJobsChanged()
    {
        // Arrange
        var viewModel = await OpenAsync();
        var loads = viewModel.LoadCount;
        viewModel.Dispose();

        // Act
        _session.NotifyJobsChanged();
        _time.Advance(QueueViewModel.RefreshInterval);

        // Assert
        Assert.Equal(loads, viewModel.LoadCount);
    }
}
