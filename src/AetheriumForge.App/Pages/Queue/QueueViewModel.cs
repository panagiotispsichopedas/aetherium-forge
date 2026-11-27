using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// The Queue page (IMPLEMENTATION_GUIDE §14): the open project's jobs with server-side filters and sort, and multi-select actions
/// (Retry, Skip, Re-queue, Change model).
/// </summary>
/// <remarks>
/// <para><b>Loading.</b> Every matching row is loaded in one query into <see cref="Rows"/>; the <c>DataGrid</c> virtualises the
/// visuals. On the reference project (5,402 rows) the query and row build take tens of milliseconds, which is cheaper and simpler
/// than paging a grid that has no data virtualisation of its own.</para>
/// <para><b>Refresh.</b> <see cref="IProjectSession.JobsChanged"/> only marks the page dirty; a one-shot timer reloads at most
/// every <see cref="RefreshInterval"/> (10 Hz). A refresh diffs by id and updates rows in place, so scroll position and
/// selection survive. A filter or sort change rebuilds the collection instead (and asks the view to restore the selection).</para>
/// <para>Timers come from the injected <see cref="TimeProvider"/> and are marshalled with <see cref="IUiDispatcher"/>, which makes
/// debounce and batching testable; in the app this behaves like a <c>DispatcherTimer</c>.</para>
/// </remarks>
public sealed partial class QueueViewModel : ObservableObject, IDisposable
{
    /// <summary>The label of the "any group" and "any kind" filter entries.</summary>
    public const string AnyValue = "(all)";

    /// <summary>The reason shown next to the actions while a run is active.</summary>
    public const string RunLockReason = "Locked while forging";

    /// <summary>The text-search debounce.</summary>
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>The minimum time between two refreshes caused by job changes.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Above this many removals, inserts and moves a refresh rebuilds the collection instead of patching it.</summary>
    internal const int MaxIncrementalChanges = 500;

    private static readonly JobState[] _retryFrom = [JobState.Failed];
    private static readonly JobState[] _skipFrom = [JobState.Queued, JobState.Failed];
    private static readonly JobState[] _requeueFrom = [JobState.Done];

    private readonly IProjectSession? _session;
    private readonly IForgeRunner? _runner;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _timeProvider;
    private readonly IUiDispatcher _ui;
    private readonly Func<IReadOnlyList<ModelInfo>> _modelSource;
    private readonly ILogger<QueueViewModel> _logger;
    private readonly HashSet<long> _selectedIds = [];
    private readonly Lock _refreshLock = new();
    private ITimer? _searchTimer;
    private ITimer? _refreshTimer;
    private bool _refreshScheduled;
    private long _loadVersion;
    private bool _disposed;
    private bool _suppressFilterEvents;
    private bool _swappingRows;
    private IReadOnlyList<long> _pendingRequeueIds = [];

    /// <summary>Initialises the view model.</summary>
    /// <param name="session">The project session, or <see langword="null"/> when none is registered yet (shows "no project").</param>
    /// <param name="runner">The pipeline runner, or <see langword="null"/> when none is registered (actions are never blocked).</param>
    /// <param name="navigation">Navigation, for the "open Scan" link.</param>
    /// <param name="timeProvider">Clock for debounce and refresh timers.</param>
    /// <param name="ui">Marshals timer and event callbacks to the UI thread.</param>
    /// <param name="modelSource">Lists the installed models for "Change model…".</param>
    /// <param name="logger">The logger.</param>
    public QueueViewModel(
        IProjectSession? session,
        IForgeRunner? runner,
        INavigationService navigation,
        TimeProvider timeProvider,
        IUiDispatcher ui,
        Func<IReadOnlyList<ModelInfo>> modelSource,
        ILogger<QueueViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(modelSource);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _runner = runner;
        _navigation = navigation;
        _timeProvider = timeProvider;
        _ui = ui;
        _modelSource = modelSource;
        _logger = logger;

        StateFilters = [.. Enum.GetValues<JobState>().Select(state => new StateFilterOption(state, OnFilterChanged))];
        Rows = [];

        if (_session is not null)
        {
            _session.Changed += OnSessionChanged;
            _session.JobsChanged += OnJobsChanged;
        }

        if (_runner is not null)
        {
            _runner.RunStateChanged += OnRunStateChanged;
            IsRunActive = _runner.State != PipelineState.Idle;
        }

        HasProject = _session?.Store is not null;
        if (HasProject)
        {
            _ui.Post(() => LoadTask = ReloadFiltersAndRowsAsync());
        }
    }

    /// <summary>Asks the view to select these rows (after a rebuild of <see cref="Rows"/>).</summary>
    public event EventHandler<IReadOnlyList<JobRowViewModel>>? SelectionRestoreRequested;

    /// <summary>Gets the rows that match the filters, in sort order.</summary>
    [ObservableProperty]
    public partial ObservableCollection<JobRowViewModel> Rows { get; private set; }

    /// <summary>Gets the state multi-select options.</summary>
    public IReadOnlyList<StateFilterOption> StateFilters { get; }

    /// <summary>Gets the group filter entries: <see cref="AnyValue"/> then every group.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Groups { get; private set; } = [AnyValue];

    /// <summary>Gets the kind filter entries: <see cref="AnyValue"/> then every kind.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Kinds { get; private set; } = [AnyValue];

    /// <summary>Gets or sets the group filter (<see cref="AnyValue"/> for any).</summary>
    [ObservableProperty]
    public partial string? SelectedGroup { get; set; } = AnyValue;

    /// <summary>Gets or sets the kind filter (<see cref="AnyValue"/> for any).</summary>
    [ObservableProperty]
    public partial string? SelectedKind { get; set; } = AnyValue;

    /// <summary>Gets or sets the path text filter; applied <see cref="SearchDebounce"/> after the last keystroke.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Gets the sort column.</summary>
    [ObservableProperty]
    public partial JobSortColumn SortColumn { get; private set; } = JobSortColumn.Id;

    /// <summary>Gets a value indicating whether the sort is descending.</summary>
    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    /// <summary>Gets a value indicating whether a project is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProject), nameof(ShowNoRows), nameof(ShowGrid))]
    public partial bool HasProject { get; private set; }

    /// <summary>Gets the number of rows that match the filters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRows), nameof(ShowGrid))]
    public partial int RowCount { get; private set; }

    /// <summary>Gets a value indicating whether a run is active (any runner state but Idle); state-changing actions are disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockReason), nameof(IsLocked))]
    public partial bool IsRunActive { get; private set; }

    /// <summary>Gets why the actions are disabled (<see cref="RunLockReason"/> during a run), or an empty string.</summary>
    public string LockReason => IsRunActive ? RunLockReason : string.Empty;

    /// <summary>Gets a value indicating whether <see cref="LockReason"/> is shown.</summary>
    public bool IsLocked => IsRunActive;

    /// <summary>Gets a value indicating whether an action is being applied.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the number of selected rows.</summary>
    [ObservableProperty]
    public partial int SelectedCount { get; private set; }

    /// <summary>Gets the time the last load took (query and row update), in milliseconds.</summary>
    [ObservableProperty]
    public partial double LastLoadMilliseconds { get; private set; }

    /// <summary>Gets the result of the last action, or an error.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the Re-queue confirmation is shown.</summary>
    [ObservableProperty]
    public partial bool IsRequeueConfirmOpen { get; private set; }

    /// <summary>Gets the Re-queue confirmation text.</summary>
    [ObservableProperty]
    public partial string RequeuePrompt { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the model picker is shown.</summary>
    [ObservableProperty]
    public partial bool IsModelPickerOpen { get; private set; }

    /// <summary>Gets the installed model names offered by the model picker.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> AvailableModels { get; private set; } = [];

    /// <summary>Gets or sets the model chosen in the model picker.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyModelCommand))]
    public partial string? SelectedModel { get; set; }

    /// <summary>Gets a value indicating whether the "no project open" empty state is shown.</summary>
    public bool ShowNoProject => !HasProject;

    /// <summary>Gets a value indicating whether the "no rows for this filter" empty state is shown.</summary>
    public bool ShowNoRows => HasProject && RowCount == 0;

    /// <summary>Gets a value indicating whether the grid is shown.</summary>
    public bool ShowGrid => HasProject && RowCount > 0;

    /// <summary>Gets the summary line, for example <c>5,402 jobs · 3 selected · loaded in 41 ms</c>.</summary>
    public string Summary => string.Create(
        CultureInfo.CurrentCulture,
        $"{RowCount:N0} jobs · {SelectedCount:N0} selected · loaded in {LastLoadMilliseconds:0} ms");

    /// <summary>Gets the task of the latest load (tests await it).</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the number of loads applied so far (tests check batching).</summary>
    internal int LoadCount { get; private set; }

    /// <summary>Updates the selection from the grid.</summary>
    /// <param name="rows">The selected rows.</param>
    public void UpdateSelection(IEnumerable<JobRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Swapping Rows makes the grid drop its selection and report an empty one; that is not the user's choice.
        if (_swappingRows)
        {
            return;
        }

        _selectedIds.Clear();
        foreach (var row in rows)
        {
            _selectedIds.Add(row.Id);
        }

        SelectedCount = _selectedIds.Count;
        NotifyActionsChanged();
    }

    /// <summary>Sorts by <paramref name="column"/>: ascending first, then toggles the direction on the same column.</summary>
    /// <param name="column">The column.</param>
    public void SortBy(JobSortColumn column)
    {
        SortDescending = SortColumn == column && !SortDescending;
        SortColumn = column;
        LoadTask = ReloadRowsAsync(rebuild: true);
    }

    /// <summary>Reloads filters and rows now.</summary>
    /// <returns>A task that completes when loaded.</returns>
    [RelayCommand]
    public Task RefreshAsync() => LoadTask = ReloadFiltersAndRowsAsync();

    /// <summary>Releases timers and event subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session.JobsChanged -= OnJobsChanged;
        }

        if (_runner is not null)
        {
            _runner.RunStateChanged -= OnRunStateChanged;
        }

        _searchTimer?.Dispose();
        lock (_refreshLock)
        {
            _refreshTimer?.Dispose();
        }
    }

    /// <summary>Builds the store query from the current filters and sort.</summary>
    /// <returns>The query.</returns>
    internal JobQuery BuildQuery()
    {
        var selected = StateFilters.Where(option => option.IsSelected).Select(option => option.State).ToHashSet();

        return new JobQuery
        {
            States = selected.Count == StateFilters.Count ? null : selected,
            Group = SelectedGroup is null or AnyValue ? null : SelectedGroup,
            Kind = SelectedKind is null or AnyValue ? null : SelectedKind,
            PathContains = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            SortBy = SortColumn,
            Descending = SortDescending,
        };
    }

    [RelayCommand]
    private void OpenScan() => _navigation.NavigateTo(AppPage.Scan);

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var option in StateFilters)
        {
            option.SetSelectedSilently(true);
        }

        _searchTimer?.Dispose();
        _searchTimer = null;
        _suppressFilterEvents = true;
        try
        {
            SearchText = string.Empty;
            SelectedGroup = AnyValue;
            SelectedKind = AnyValue;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        LoadTask = ReloadRowsAsync(rebuild: true);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => ApplyStateAsync(JobState.Queued, _retryFrom, "Retry", "queued for retry");

    private bool CanRetry() => CanAct(_retryFrom);

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private Task SkipAsync() => ApplyStateAsync(JobState.Skipped, _skipFrom, "Skip", "skipped");

    private bool CanSkip() => CanAct(_skipFrom);

    [RelayCommand(CanExecute = nameof(CanRequeue))]
    private void Requeue()
    {
        _pendingRequeueIds = [.. SelectedRows(_requeueFrom).Select(row => row.Id)];
        RequeuePrompt = string.Create(
            CultureInfo.CurrentCulture,
            $"Re-queue {_pendingRequeueIds.Count:N0} done job(s)? Their output files under {OutputTexturesFolder()} will be deleted and the textures processed again.");
        IsRequeueConfirmOpen = true;
    }

    private bool CanRequeue() => CanAct(_requeueFrom);

    [RelayCommand]
    private void CancelRequeue()
    {
        _pendingRequeueIds = [];
        IsRequeueConfirmOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmRequeueAsync()
    {
        IsRequeueConfirmOpen = false;
        var ids = _pendingRequeueIds.ToHashSet();
        _pendingRequeueIds = [];
        if (ids.Count == 0 || _session?.Store is not { } store || _session.Current is not { } project || IsRunActive)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Only rows that are still Done: a row may have changed state since the prompt was shown.
            var rows = Rows.Where(row => ids.Contains(row.Id) && row.State == JobState.Done).ToList();
            var accepted = new List<(long Id, string File)>();
            var rejected = 0;
            foreach (var row in rows)
            {
                if (QueueOutputPaths.TryResolve(project.OutputRoot, row.Path, out var file))
                {
                    accepted.Add((row.Id, file));
                }
                else
                {
                    rejected++;
                    LogRejectedOutputPath(row.Path, project.OutputRoot);
                }
            }

            // State first: a Queued job whose old output survives is simply overwritten by the next run, whereas a Done job
            // whose output was deleted would never be redone.
            // The store re-checks Done inside its transaction and reports which jobs it changed; only their outputs are deleted.
            var changedIds = (await Task.Run(() => store.SetStateReturningIdsAsync([.. accepted.Select(item => item.Id)], JobState.Queued, null, _requeueFrom)).ConfigureAwait(true))
                .ToHashSet();
            var outputRoot = project.OutputRoot;
            var deleteFailures = await Task.Run(() => DeleteOutputs(outputRoot, accepted.Where(item => changedIds.Contains(item.Id)).Select(item => item.File))).ConfigureAwait(true);

            StatusMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"Re-queue: {changedIds.Count:N0} job(s) queued{(rejected > 0 ? $", {rejected:N0} rejected (output path outside {OutputTexturesFolder()})" : string.Empty)}{(deleteFailures > 0 ? $", {deleteFailures:N0} output file(s) not deleted (see log)" : string.Empty)}.");
            _session.NotifyJobsChanged();
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException)
        {
            ReportActionFailure("Re-queue", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeModel))]
    private async Task ChangeModelAsync()
    {
        // Discovery reads the models folder: keep that I/O off the UI thread.
        List<string> models;
        IsBusy = true;
        try
        {
            models = await Task.Run(() => _modelSource().Select(model => model.Name).ToList()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportActionFailure("Change model", ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (IsRunActive)
        {
            return;
        }

        AvailableModels = models;
        SelectedModel = models.Count > 0 ? models[0] : null;
        if (models.Count == 0)
        {
            StatusMessage = $"No models installed in {ModelCatalog.DefaultModelsFolder}.";
            return;
        }

        IsModelPickerOpen = true;
    }

    private bool CanChangeModel() => CanAct(null);

    [RelayCommand]
    private void CancelModel() => IsModelPickerOpen = false;

    [RelayCommand(CanExecute = nameof(CanApplyModel))]
    private async Task ApplyModelAsync()
    {
        IsModelPickerOpen = false;
        if (SelectedModel is not { } model || _session?.Store is not { } store || IsRunActive)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ids = SelectedRows(null).Select(row => row.Id).ToList();
            var changed = await Task.Run(() => store.SetModelAsync(ids, model)).ConfigureAwait(true);
            StatusMessage = string.Create(CultureInfo.CurrentCulture, $"Change model: {changed:N0} job(s) now use {model}.");
            _session.NotifyJobsChanged();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            ReportActionFailure("Change model", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyModel() => !string.IsNullOrWhiteSpace(SelectedModel);

    private async Task ApplyStateAsync(JobState state, JobState[] from, string action, string verb)
    {
        if (_session?.Store is not { } store || IsRunActive)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ids = SelectedRows(from).Select(row => row.Id).ToList();
            var changed = await Task.Run(() => store.SetStateAsync(ids, state, null, from)).ConfigureAwait(true);
            StatusMessage = string.Create(CultureInfo.CurrentCulture, $"{action}: {changed:N0} job(s) {verb}.");
            _session.NotifyJobsChanged();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            ReportActionFailure(action, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportActionFailure(string action, Exception ex)
    {
        LogActionFailed(ex, action);
        StatusMessage = $"{action} failed: {ex.Message}";
    }

    private int DeleteOutputs(string outputRoot, IEnumerable<string> files)
    {
        var failures = 0;
        foreach (var file in files)
        {
            try
            {
                if (!QueueOutputPaths.IsFreeOfLinks(outputRoot, file))
                {
                    failures++;
                    LogLinkedOutputSkipped(file);
                    continue;
                }

                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                LogDeleteFailed(ex, file);
            }
        }

        return failures;
    }

    private string OutputTexturesFolder() =>
        _session?.Current is { } project ? Path.Combine(project.OutputRoot, QueueOutputPaths.TexturesFolder) : QueueOutputPaths.TexturesFolder;

    private bool CanAct(JobState[]? from) =>
        HasProject && !IsRunActive && !IsBusy && _selectedIds.Count > 0 && SelectedRows(from).Any();

    private IEnumerable<JobRowViewModel> SelectedRows(JobState[]? from) =>
        Rows.Where(row => _selectedIds.Contains(row.Id) && row.State != JobState.Running && (from is null || from.Contains(row.State)));

    private void NotifyActionsChanged()
    {
        RetryCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        RequeueCommand.NotifyCanExecuteChanged();
        ChangeModelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnIsRunActiveChanged(bool value)
    {
        if (value)
        {
            IsRequeueConfirmOpen = false;
            IsModelPickerOpen = false;
            _pendingRequeueIds = [];
        }

        NotifyActionsChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyActionsChanged();

    partial void OnRowCountChanged(int value) => OnPropertyChanged(nameof(Summary));

    partial void OnLastLoadMillisecondsChanged(double value) => OnPropertyChanged(nameof(Summary));

    partial void OnSelectedGroupChanged(string? value) => OnFilterChanged();

    partial void OnSelectedKindChanged(string? value) => OnFilterChanged();

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        if (_searchTimer is null)
        {
            _searchTimer = _timeProvider.CreateTimer(_ => _ui.Post(OnSearchDebounced), null, SearchDebounce, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _searchTimer.Change(SearchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSearchDebounced()
    {
        if (!_disposed)
        {
            LoadTask = ReloadRowsAsync(rebuild: true);
        }
    }

    private void OnFilterChanged()
    {
        if (_suppressFilterEvents || _disposed)
        {
            return;
        }

        LoadTask = ReloadRowsAsync(rebuild: true);
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            HasProject = _session?.Store is not null;
            _selectedIds.Clear();
            SelectedCount = 0;
            LoadTask = ReloadFiltersAndRowsAsync();
        });
    }

    private void OnJobsChanged(object? sender, EventArgs e)
    {
        lock (_refreshLock)
        {
            if (_refreshScheduled || _disposed)
            {
                return;
            }

            _refreshScheduled = true;
            if (_refreshTimer is null)
            {
                _refreshTimer = _timeProvider.CreateTimer(_ => OnRefreshTick(), null, RefreshInterval, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _refreshTimer.Change(RefreshInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnRefreshTick()
    {
        lock (_refreshLock)
        {
            _refreshScheduled = false;
        }

        _ui.Post(() =>
        {
            if (!_disposed)
            {
                LoadTask = ReloadRowsAsync(rebuild: false);
            }
        });
    }

    private void OnRunStateChanged(object? sender, PipelineRunSummary? e)
    {
        var active = _runner is not null && _runner.State != PipelineState.Idle;
        _ui.Post(() => IsRunActive = active);
    }

    private async Task ReloadFiltersAndRowsAsync()
    {
        if (_session?.Store is { } store)
        {
            try
            {
                var groups = await Task.Run(() => store.GetDistinctGroupsAsync()).ConfigureAwait(true);
                var kinds = await Task.Run(() => store.GetDistinctKindsAsync()).ConfigureAwait(true);
                _suppressFilterEvents = true;
                try
                {
                    Groups = [AnyValue, .. groups];
                    Kinds = [AnyValue, .. kinds];
                    if (SelectedGroup is not null && !Groups.Contains(SelectedGroup))
                    {
                        SelectedGroup = AnyValue;
                    }

                    if (SelectedKind is not null && !Kinds.Contains(SelectedKind))
                    {
                        SelectedKind = AnyValue;
                    }
                }
                finally
                {
                    _suppressFilterEvents = false;
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                ReportLoadFailure(ex);
                return;
            }
        }
        else
        {
            Groups = [AnyValue];
            Kinds = [AnyValue];
        }

        await ReloadRowsAsync(rebuild: true).ConfigureAwait(true);
    }

    private async Task ReloadRowsAsync(bool rebuild)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        if (_session?.Store is not { } store)
        {
            HasProject = false;
            Rows = [];
            RowCount = 0;
            return;
        }

        HasProject = true;
        var query = BuildQuery();
        var stopwatch = Stopwatch.StartNew();
        List<JobRecord> records;
        IReadOnlyDictionary<JobState, int> counts;
        try
        {
            (records, counts) = await Task.Run(async () =>
            {
                var list = new List<JobRecord>();
                await foreach (var job in store.QueryJobsAsync(query).ConfigureAwait(false))
                {
                    list.Add(job);
                }

                return (list, await store.CountByStateAsync(query).ConfigureAwait(false));
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            ReportLoadFailure(ex);
            return;
        }

        if (version != Interlocked.Read(ref _loadVersion) || _disposed)
        {
            return;
        }

        foreach (var option in StateFilters)
        {
            option.Count = counts[option.State];
        }

        MergeRows(records, rebuild);
        LoadCount++;
        RowCount = Rows.Count;
        LastLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        NotifyActionsChanged();
    }

    private void ReportLoadFailure(Exception ex)
    {
        LogLoadFailed(ex);
        StatusMessage = $"Could not load jobs: {ex.Message}";
    }

    /// <summary>Applies freshly loaded records to <see cref="Rows"/>, patching in place unless asked (or forced) to rebuild.</summary>
    private void MergeRows(List<JobRecord> records, bool rebuild)
    {
        var byId = Rows.ToDictionary(row => row.Id);
        if (!rebuild && !TryPatchRows(records, byId))
        {
            rebuild = true;
        }

        if (!rebuild)
        {
            return;
        }

        var rows = new List<JobRowViewModel>(records.Count);
        foreach (var record in records)
        {
            rows.Add(byId.TryGetValue(record.Id, out var row) ? row.Update(record) : new JobRowViewModel(record));
        }

        // Work out what to keep before the swap: the grid clears its selection when ItemsSource changes, and that report is ignored.
        var keep = rows.Where(row => _selectedIds.Contains(row.Id)).ToList();
        _swappingRows = true;
        try
        {
            Rows = new ObservableCollection<JobRowViewModel>(rows);
        }
        finally
        {
            _swappingRows = false;
        }

        // Rows filtered out are no longer selected; the count must match what the grid shows.
        _selectedIds.IntersectWith(keep.Select(row => row.Id));
        SelectedCount = _selectedIds.Count;
        if (keep.Count > 0)
        {
            SelectionRestoreRequested?.Invoke(this, keep);
        }
    }

    private bool TryPatchRows(List<JobRecord> records, Dictionary<long, JobRowViewModel> byId)
    {
        var newIds = records.Select(record => record.Id).ToHashSet();
        var removals = Rows.Count(row => !newIds.Contains(row.Id));
        var inserts = records.Count(record => !byId.ContainsKey(record.Id));
        if (removals + inserts > MaxIncrementalChanges)
        {
            return false;
        }

        for (var index = Rows.Count - 1; index >= 0; index--)
        {
            if (!newIds.Contains(Rows[index].Id))
            {
                Rows.RemoveAt(index);
            }
        }

        var moves = 0;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (index < Rows.Count && Rows[index].Id == record.Id)
            {
                Rows[index].Update(record);
                continue;
            }

            if (byId.TryGetValue(record.Id, out var row))
            {
                if (++moves + removals + inserts > MaxIncrementalChanges)
                {
                    return false;
                }

                var oldIndex = IndexOf(row, index + 1);
                Rows.Move(oldIndex, index);
                row.Update(record);
            }
            else
            {
                Rows.Insert(index, new JobRowViewModel(record));
            }
        }

        return true;
    }

    private int IndexOf(JobRowViewModel row, int start)
    {
        for (var index = start; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index], row))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Row {row.Id} vanished while patching the queue.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-queue rejected {JobPath}: its output would be outside {OutputRoot}\\textures")]
    private partial void LogRejectedOutputPath(string jobPath, string outputRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-queue did not delete {OutputFile}: a junction or symbolic link lies on its path")]
    private partial void LogLinkedOutputSkipped(string outputFile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-queue could not delete {OutputFile}")]
    private partial void LogDeleteFailed(Exception exception, string outputFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queue action {Action} failed")]
    private partial void LogActionFailed(Exception exception, string action);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading the job queue failed")]
    private partial void LogLoadFailed(Exception exception);
}
