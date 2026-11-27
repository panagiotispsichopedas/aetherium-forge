using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// The Rules page (IMPLEMENTATION_GUIDE §8, §14 "Rules"): edits groups, exclusions and the minimum source size, shows live counts and
/// estimates (recomputed in the background 300 ms after the last edit) and applies the rules to the project's jobs.
/// </summary>
public sealed partial class RulesViewModel : ObservableObject, IDisposable
{
    /// <summary>The debounce between the last edit and the background recount.</summary>
    public static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>While a run is active, job-row reloads are coalesced to at most one per this interval.</summary>
    public static readonly TimeSpan ReloadCoalesceDelay = TimeSpan.FromSeconds(1);

    /// <summary>Apply asks first when it would newly skip more than this many textures.</summary>
    public const int MassSkipCount = 50;

    /// <summary>Apply asks first when it would newly skip more than this fraction of all textures.</summary>
    public const double MassSkipFraction = 0.10;

    private readonly IProjectSession _session;
    private readonly INavigationService _navigation;
    private readonly IRulesPageServices _services;
    private readonly TimeProvider _timeProvider;
    private readonly IForgeRunner? _runner;
    private readonly ILogger<RulesViewModel> _logger;

    private ForgeRules _baseline = ForgeRules.Default;
    private IReadOnlyList<JobRecord> _rows = [];
    private Func<string, double?>? _timings;
    private CancellationTokenSource? _recomputeCts;
    private ProjectInfo? _loadedProject;
    private bool _rowsStale;
    private bool _reloading;
    private int _rowsGeneration;
    private bool _populating;

    /// <summary>Initialises the page.</summary>
    /// <param name="session">The open project.</param>
    /// <param name="navigation">Navigation (Compare link; reload when the page is shown).</param>
    /// <param name="services">Dialogs, pickers, models, timings and UI dispatch.</param>
    /// <param name="timeProvider">Clock for the recount debounce.</param>
    /// <param name="serviceProvider">Resolves the optional <see cref="IForgeRunner"/> (Apply is disabled while a run is active).</param>
    /// <param name="logger">The logger.</param>
    public RulesViewModel(
        IProjectSession session,
        INavigationService navigation,
        IRulesPageServices services,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider,
        ILogger<RulesViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _navigation = navigation;
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
        _runner = serviceProvider.GetService<IForgeRunner>();

        ExclusionsText = string.Empty;
        MinSourceText = string.Empty;
        UpdateRunState();

        _session.Changed += OnSessionChanged;
        _session.JobsChanged += OnJobsChanged;
        _navigation.Navigated += OnNavigated;
        if (_runner is not null)
        {
            _runner.RunStateChanged += OnRunStateChanged;
        }
    }

    /// <summary>Gets the groups in evaluation order (the first match wins).</summary>
    public ObservableCollection<RuleGroupViewModel> Groups { get; } = [];

    /// <summary>Gets the installed models plus the models the rules use.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>Gets or sets the selected group.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateGroupCommand), nameof(DeleteGroupCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial RuleGroupViewModel? SelectedGroup { get; set; }

    /// <summary>Gets or sets the excluded folder prefixes, one per line.</summary>
    [ObservableProperty]
    public partial string ExclusionsText { get; set; }

    /// <summary>Gets or sets the minimum source max dimension.</summary>
    [ObservableProperty]
    public partial string MinSourceText { get; set; }

    /// <summary>Gets the first problem with the rules, or <see langword="null"/> when they are valid.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ExportCommand))]
    public partial string? ValidationError { get; private set; }

    /// <summary>Gets a value indicating whether the editor differs from the rules stored in the project.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>Gets a value indicating whether a project is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasProject { get; private set; }

    /// <summary>Gets a value indicating whether a pipeline run is active (Apply is disabled).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsRunActive { get; private set; }

    /// <summary>Gets a value indicating whether a load or apply is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the latest preview, or <see langword="null"/> before the first count.</summary>
    [ObservableProperty]
    public partial RulesPreview? Preview { get; private set; }

    /// <summary>Gets the skipped-count line.</summary>
    [ObservableProperty]
    public partial string SkippedText { get; private set; } = "…";

    /// <summary>Gets the upscaled-count line.</summary>
    [ObservableProperty]
    public partial string UpscaledText { get; private set; } = "…";

    /// <summary>Gets the estimated output size line.</summary>
    [ObservableProperty]
    public partial string SizeText { get; private set; } = "…";

    /// <summary>Gets the estimated time line.</summary>
    [ObservableProperty]
    public partial string TimeText { get; private set; } = "…";

    /// <summary>Gets the last status message (applied, imported, errors).</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets the latest background recount (tests await it).</summary>
    internal Task PendingRecompute { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the rules stored in the project (the Revert target).</summary>
    internal ForgeRules Baseline => _baseline;

    private bool CanApply => HasProject && !IsRunActive && !IsBusy && ValidationError is null;

    private bool HasSelection => SelectedGroup is not null;

    private bool CanMoveUp => SelectedGroup is { } g && Groups.IndexOf(g) > 0;

    private bool CanMoveDown => SelectedGroup is { } g && Groups.IndexOf(g) is int i && i >= 0 && i < Groups.Count - 1;

    private bool CanExport => ValidationError is null;

    /// <summary>
    /// Loads the project's rules (the stored <c>rulesJson</c>, else the session's), its job rows, the model list and the GPU timings, and
    /// recounts. Unsaved edits are replaced.
    /// </summary>
    /// <returns>A task that completes when loaded; the recount follows after <see cref="RecomputeDelay"/>.</returns>
    public async Task LoadAsync()
    {
        var project = _session.Current;
        var store = _session.Store;
        _loadedProject = project;
        HasProject = project is not null && store is not null;
        if (project is null || store is null)
        {
            _rows = [];
            Populate(ForgeRules.Default);
            _baseline = ForgeRules.Default;
            StatusMessage = "Open or scan a project to edit its rules.";
            return;
        }

        IsBusy = true;
        try
        {
            var rules = await ReadStoredRulesAsync(store).ConfigureAwait(true) ?? project.Rules;
            int generation = _rowsGeneration;
            _rows = await ReadRowsAsync(store).ConfigureAwait(true);
            _rowsStale = generation != _rowsGeneration;
            var timings = await _services.LoadSecondsPerMegapixelAsync().ConfigureAwait(true);
            _timings = timings.Count == 0 ? null : model => timings.TryGetValue(GpuProfiler.GetModelFamily(model), out double spm) ? spm : null;
            LoadModels(rules);

            _baseline = rules;
            Populate(rules);
            StatusMessage = null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            RulesLog.LoadFailed(_logger, ex, project.Root);
            StatusMessage = $"Could not load the project rules: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Releases event subscriptions and cancels a pending recount.</summary>
    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _session.JobsChanged -= OnJobsChanged;
        _navigation.Navigated -= OnNavigated;
        if (_runner is not null)
        {
            _runner.RunStateChanged -= OnRunStateChanged;
        }

        _recomputeCts?.Cancel();
        _recomputeCts?.Dispose();
        _recomputeCts = null;
    }

    /// <summary>
    /// Builds the rules from the editor.
    /// </summary>
    /// <param name="error">The first problem, or <see langword="null"/>.</param>
    /// <returns>The rules, or <see langword="null"/> when they are not valid.</returns>
    internal ForgeRules? BuildRules(out string? error)
    {
        if (!int.TryParse(MinSourceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minSource))
        {
            error = "The minimum source size must be a whole number.";
            return null;
        }

        var groups = new List<GroupRule>(Groups.Count);
        foreach (var group in Groups)
        {
            var rule = group.TryBuild(out error);
            if (rule is null || error is not null)
            {
                return null;
            }

            groups.Add(rule);
        }

        var rules = new ForgeRules
        {
            Groups = groups,
            MinSource = minSource,
            Exclude = [.. RuleGroupViewModel.SplitLines(ExclusionsText)],
        };
        error = RulesApplier.Validate(rules);
        return error is null ? rules : null;
    }

    [RelayCommand]
    private void AddGroup()
    {
        var group = new RuleGroupViewModel(new GroupRule { Name = UniqueName("New group"), Model = AvailableModels.FirstOrDefault() ?? "realesrgan-x4plus" });
        int index = SelectedGroup is { } selected ? Groups.IndexOf(selected) + 1 : Groups.Count;
        InsertGroup(index, group);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateGroup()
    {
        if (SelectedGroup is not { } selected || selected.TryBuild(out _) is not { } rule)
        {
            StatusMessage = "Fix the group's values before duplicating it.";
            return;
        }

        InsertGroup(Groups.IndexOf(selected) + 1, new RuleGroupViewModel(rule with { Name = UniqueName($"{rule.Name} copy") }));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is not { } selected
            || !await _services.ConfirmAsync("Delete group", $"Delete the group '{selected.Name}'? Nothing is saved until you apply.").ConfigureAwait(true)
            || !Groups.Contains(selected))
        {
            return;
        }

        int index = Groups.IndexOf(selected);
        selected.Edited -= OnGroupEdited;
        Groups.RemoveAt(index);
        SelectedGroup = Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)];
        OnEdited();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveSelected(1);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_session.Store is not { } store)
        {
            StatusMessage = "Open a project first.";
            return;
        }

        if (BuildRules(out string? error) is not { } rules)
        {
            ValidationError = error;
            return;
        }

        IsBusy = true;
        try
        {
            // Looks are edited on Compare; take the project's current looks so this Apply does not overwrite them.
            var stored = await ReadStoredRulesAsync(store).ConfigureAwait(true) ?? _baseline;
            rules = MergeLooks(stored, rules);
            var rows = await ReadRowsAsync(store).ConfigureAwait(true);
            var options = new RulesPreviewOptions { SecondsPerSourceMegapixel = _timings, DoneLookDiffers = RulesApplier.LookDiffersFrom(stored) };
            var preview = await Task.Run(() => RulesApplier.Preview(rows, rules, options)).ConfigureAwait(true);

            var changes = new List<JobRuleChange>(preview.Changes);
            int done = preview.DoneChanges.Count;
            bool requeue = done > 0 && await _services.ConfirmAsync(
                "Finished textures would change",
                $"{done.ToString("N0", CultureInfo.CurrentCulture)} finished textures would change. Re-queue them?").ConfigureAwait(true);
            if (requeue)
            {
                changes.AddRange(preview.DoneChanges);
            }

            int newlySkipped = changes.Count(c => c.NewState == JobState.Skipped && c.OldState != JobState.Skipped);
            if ((newlySkipped > MassSkipCount || newlySkipped > preview.TotalJobs * MassSkipFraction)
                && !await _services.ConfirmAsync(
                    "Many textures would be skipped",
                    $"{newlySkipped.ToString("N0", CultureInfo.CurrentCulture)} textures would be skipped. Apply anyway?").ConfigureAwait(true))
            {
                StatusMessage = "Apply cancelled; nothing was changed.";
                return;
            }

            // A run may have started while a dialog was open; the store never touches Running rows, but the rules must not change mid-run.
            if (IsRunActive)
            {
                StatusMessage = "A run started; apply again when it ends.";
                return;
            }

            int updated = await store.ApplyRuleChangesAsync(changes, rules.ToJson(), CancellationToken.None).ConfigureAwait(true);
            RulesLog.Applied(_logger, updated, done, requeue);

            _baseline = rules;
            IsDirty = false;
            StatusMessage = done > 0 && !requeue
                ? $"Rules applied: {updated:N0} jobs updated; {done:N0} finished textures kept."
                : $"Rules applied: {updated:N0} jobs updated.";
            _session.NotifyJobsChanged();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            RulesLog.ApplyFailed(_logger, ex);
            StatusMessage = $"Applying the rules failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (await _services.ConfirmAsync("Reset rules", "Replace the groups, exclusions and minimum size with the defaults? Nothing is saved until you apply.").ConfigureAwait(true))
        {
            Populate(ForgeRules.Default);
            StatusMessage = "Defaults loaded; apply to save them.";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        string? json;
        try
        {
            json = await _services.OpenJsonAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not read the file: {ex.Message}";
            return;
        }

        if (json is null)
        {
            return;
        }

        try
        {
            var rules = ForgeRules.FromJson(json);
            LoadModels(rules);
            Populate(rules);
            StatusMessage = "Rules imported; apply to save them.";
        }
        catch (JsonException ex)
        {
            StatusMessage = $"The file is not valid rules JSON: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (BuildRules(out string? error) is not { } rules)
        {
            StatusMessage = error;
            return;
        }

        try
        {
            if (await _services.SaveJsonAsync("forge-rules.json", rules.ToJson()).ConfigureAwait(true))
            {
                StatusMessage = "Rules exported.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not write the file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Revert()
    {
        Populate(_baseline);
        StatusMessage = "Unsaved edits reverted.";
    }

    [RelayCommand]
    private void OpenCompare() => _navigation.NavigateTo(AppPage.Compare);

    partial void OnSelectedGroupChanged(RuleGroupViewModel? value)
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnExclusionsTextChanged(string value) => OnEdited();

    partial void OnMinSourceTextChanged(string value) => OnEdited();

    private static async Task<IReadOnlyList<JobRecord>> ReadRowsAsync(JobStore store) =>
        await Task.Run(async () =>
        {
            var rows = new List<JobRecord>();
            await foreach (var job in store.GetJobsAsync().ConfigureAwait(false))
            {
                rows.Add(job);
            }

            return (IReadOnlyList<JobRecord>)rows;
        }).ConfigureAwait(true);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0} KB"),
    };

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalHours} h {span.Minutes} min")
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(span.Minutes, seconds > 0 ? 1 : 0)} min");
    }

    private async Task<ForgeRules?> ReadStoredRulesAsync(JobStore store)
    {
        if (await store.GetProjectValueAsync(RulesApplier.RulesProjectKey).ConfigureAwait(true) is not { } json)
        {
            return null;
        }

        try
        {
            return ForgeRules.FromJson(json);
        }
        catch (JsonException ex)
        {
            RulesLog.StoredRulesInvalid(_logger, ex);
            return null;
        }
    }

    private ForgeRules MergeLooks(ForgeRules stored, ForgeRules rules)
    {
        var looks = stored.Groups.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Look, StringComparer.OrdinalIgnoreCase);
        var merged = rules with { Groups = [.. rules.Groups.Select(g => looks.TryGetValue(g.Name, out var look) ? g with { Look = look } : g)] };
        for (int i = 0; i < Groups.Count && i < merged.Groups.Count; i++)
        {
            Groups[i].SetLook(merged.Groups[i].Look);
        }

        return merged;
    }

    private void LoadModels(ForgeRules rules)
    {
        var names = new SortedSet<string>(_services.DiscoverModels(), StringComparer.OrdinalIgnoreCase);
        names.UnionWith(rules.Groups.Select(g => g.Model).Where(m => !string.IsNullOrWhiteSpace(m)));
        AvailableModels.Clear();
        foreach (string name in names)
        {
            AvailableModels.Add(name);
        }
    }

    private void Populate(ForgeRules rules)
    {
        _populating = true;
        try
        {
            foreach (var group in Groups)
            {
                group.Edited -= OnGroupEdited;
            }

            Groups.Clear();
            foreach (var rule in rules.Groups)
            {
                var group = new RuleGroupViewModel(rule);
                group.Edited += OnGroupEdited;
                Groups.Add(group);
            }

            ExclusionsText = string.Join(Environment.NewLine, rules.Exclude);
            MinSourceText = rules.MinSource.ToString(CultureInfo.InvariantCulture);
            SelectedGroup = Groups.FirstOrDefault();
        }
        finally
        {
            _populating = false;
        }

        OnEdited();
    }

    private void InsertGroup(int index, RuleGroupViewModel group)
    {
        group.Edited += OnGroupEdited;
        Groups.Insert(index, group);
        SelectedGroup = group;
        OnEdited();
    }

    private void MoveSelected(int offset)
    {
        if (SelectedGroup is not { } selected)
        {
            return;
        }

        int from = Groups.IndexOf(selected);
        int to = from + offset;
        if (to < 0 || to >= Groups.Count)
        {
            return;
        }

        Groups.Move(from, to);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        OnEdited();
    }

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int n = 2; Groups.Any(g => string.Equals(g.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {n}");
        }

        return candidate;
    }

    private void OnGroupEdited(object? sender, EventArgs e) => OnEdited();

    private void OnEdited()
    {
        if (_populating)
        {
            return;
        }

        // Validation is immediate; the recount waits for the debounce and runs in the background.
        var rules = BuildRules(out string? error);
        ValidationError = error;
        IsDirty = rules is null || !rules.Equals(_baseline);
        ScheduleRecompute();
    }

    private void ScheduleRecompute()
    {
        _recomputeCts?.Cancel();
        _recomputeCts?.Dispose();
        _recomputeCts = new CancellationTokenSource();
        PendingRecompute = RecomputeAsync(_recomputeCts.Token);
    }

    private async Task RecomputeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RecomputeDelay, _timeProvider, ct).ConfigureAwait(true);
            if (BuildRules(out _) is not { } rules)
            {
                return;
            }

            var rows = _rows;
            var options = new RulesPreviewOptions { SecondsPerSourceMegapixel = _timings, DoneLookDiffers = RulesApplier.LookDiffersFrom(_baseline) };
            var preview = await Task.Run(() => RulesApplier.Preview(rows, rules, options, ct), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            ShowPreview(preview);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer edit superseded this recount; it runs its own.
        }
    }

    private void ShowPreview(RulesPreview preview)
    {
        Preview = preview;
        for (int i = 0; i < Groups.Count; i++)
        {
            Groups[i].Count = i < preview.Groups.Count ? preview.Groups[i].Count : null;
        }

        var culture = CultureInfo.CurrentCulture;
        UpscaledText = string.Create(culture, $"{preview.UpscaledCount:N0} of {preview.TotalJobs:N0} textures upscaled");
        SkippedText = preview.NotRuleManaged > 0
            ? string.Create(culture, $"{preview.SkippedCount:N0} skipped by the rules, {preview.NotRuleManaged:N0} skipped by the scan")
            : string.Create(culture, $"{preview.SkippedCount:N0} skipped by the rules");
        SizeText = $"≈ {FormatBytes(preview.EstimatedBytes)} output (BC7 with mips)";
        TimeText = preview.EstimatedSeconds is double seconds
            ? preview.UntimedCount > 0
                ? string.Create(culture, $"≈ {FormatDuration(seconds)} remaining (+{preview.UntimedCount:N0} with an uncalibrated model)")
                : $"≈ {FormatDuration(seconds)} remaining"
            : "No time estimate: calibrate the GPU on the Models page";
    }

    private void UpdateRunState() => IsRunActive = _runner is not null && _runner.State != PipelineState.Idle;

    private async Task ReloadRowsAsync()
    {
        if (_reloading)
        {
            // The running reload loops until it has read the newest generation.
            return;
        }

        _reloading = true;
        try
        {
            IReadOnlyList<JobRecord> rows;
            int generation;
            do
            {
                // While a run is active the pipeline changes rows constantly; read at most once per ReloadCoalesceDelay.
                if (IsRunActive)
                {
                    await Task.Delay(ReloadCoalesceDelay, _timeProvider).ConfigureAwait(true);
                }

                generation = _rowsGeneration;
                if (_session.Store is not { } store)
                {
                    return;
                }

                rows = await ReadRowsAsync(store).ConfigureAwait(true);
            }
            while (generation != _rowsGeneration);

            // Rows read for an older generation (a newer change arrived meanwhile) were dropped by the loop above.
            _rows = rows;
            _rowsStale = false;
            ScheduleRecompute();
        }
        catch (SqliteException ex)
        {
            RulesLog.ReloadFailed(_logger, ex);
            StatusMessage = $"Could not reload the jobs: {ex.Message}";
        }
        finally
        {
            _reloading = false;
        }
    }

    private async Task RefreshLooksAsync()
    {
        if (_session.Store is not { } store || await ReadStoredRulesAsync(store).ConfigureAwait(true) is not { } stored)
        {
            return;
        }

        var looks = stored.Groups.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Look, StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            if (looks.TryGetValue(group.Name.Trim(), out var look))
            {
                group.SetLook(look);
            }
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _services.PostToUi(() => _ = RunSafeAsync(LoadAsync));

    private void OnJobsChanged(object? sender, EventArgs e) => _services.PostToUi(() =>
    {
        _rowsGeneration++;
        if (_navigation.Current == AppPage.Rules)
        {
            _ = RunSafeAsync(ReloadRowsAsync);
        }
        else
        {
            _rowsStale = true;
        }
    });

    private void OnNavigated(object? sender, AppPage page)
    {
        if (page != AppPage.Rules)
        {
            return;
        }

        if (!ReferenceEquals(_loadedProject, _session.Current))
        {
            _ = RunSafeAsync(LoadAsync);
        }
        else
        {
            _ = RunSafeAsync(async () =>
            {
                await RefreshLooksAsync().ConfigureAwait(true);
                if (_rowsStale)
                {
                    await ReloadRowsAsync().ConfigureAwait(true);
                }
            });
        }
    }

    private void OnRunStateChanged(object? sender, PipelineRunSummary? e) => _services.PostToUi(UpdateRunState);

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            RulesLog.BackgroundFailed(_logger, ex);
            StatusMessage = $"Something went wrong: {ex.Message}";
        }
    }
}
