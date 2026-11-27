using System.Collections.ObjectModel;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The Compare page (IMPLEMENTATION_GUIDE §14): pick a finished job or preview random samples of a group, compare before/after with a
/// split slider, zoom, pan and channel view, and direct the group's look and model with live preview.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    /// <summary>The smallest zoom, in percent of actual pixels.</summary>
    public const int MinZoom = 100;

    /// <summary>The largest zoom, in percent of actual pixels.</summary>
    public const int MaxZoom = 400;

    /// <summary>The default number of random samples.</summary>
    public const int DefaultSampleCount = 10;

    /// <summary>The start of the question asked after Apply when finished textures are affected ("Apply to N finished textures?").</summary>
    public const string ApplyToFinishedQuestion = "Apply to";

    /// <summary>How many affected file names the confirmation lists.</summary>
    public const int AffectedListLimit = 8;

    /// <summary>The banner shown while a pipeline run is active.</summary>
    public const string RunActiveReason = "A run is active. New GPU previews and rule changes (Apply, Use model) are locked until it ends; cached previews still work.";

    private const int SearchLimit = 200;
    private const int ThumbnailSide = 128;
    private static readonly int[] ZoomSteps = [100, 150, 200, 300, 400];

    private readonly PreviewEngine _engine;
    private readonly ICompareProjectData _data;
    private readonly ICompareDialogs _dialogs;
    private readonly Random _random;
    private readonly ILookScopeStore _looks;
    private readonly Stack<(LookOverrides Overrides, PreviewRender Render)> _undo = new();
    private readonly ILogger<CompareViewModel> _logger;
    private readonly IForgeRunner? _runner;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _loadCancellation;
    private PreviewSample? _sample;
    private PreviewRender? _render;
    private LookOverrides? _renderedOverrides;
    private ForgeRules? _rules;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompareViewModel"/> class.
    /// </summary>
    /// <param name="engine">The preview engine.</param>
    /// <param name="data">The project rules and jobs.</param>
    /// <param name="dialogs">Confirmation prompts.</param>
    /// <param name="random">Picks random samples; inject a seeded instance for deterministic picks.</param>
    /// <param name="lookStore">Where scope looks are stored; defaults to <see cref="CoreLookScopeStore"/> over <paramref name="data"/>.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    /// <param name="runner">The pipeline runner, or <see langword="null"/> when none is registered; its state locks GPU previews and rule changes.</param>
    public CompareViewModel(
        PreviewEngine engine,
        ICompareProjectData data,
        ICompareDialogs dialogs,
        Random? random = null,
        ILookScopeStore? lookStore = null,
        ILogger<CompareViewModel>? logger = null,
        IForgeRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(dialogs);

        _engine = engine;
        _data = data;
        _dialogs = dialogs;
        _random = random ?? Random.Shared;
        _looks = lookStore ?? new CoreLookScopeStore(data);
        _logger = logger ?? NullLogger<CompareViewModel>.Instance;
        _runner = runner;
        _uiContext = SynchronizationContext.Current;

        Look.LookChanged += (_, _) => UpdateRenderState();
        Look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LookPanelViewModel.Scope))
            {
                ClearUndo();
            }
            else if (e.PropertyName == nameof(LookPanelViewModel.IsScopeDirty))
            {
                ApplyCommand.NotifyCanExecuteChanged();
                RevertCommand.NotifyCanExecuteChanged();
            }
        };
        _data.Changed += (_, _) => _ = RefreshAsync();
        if (runner is not null)
        {
            // Raised on any thread: hop to the UI thread when there is one.
            runner.RunStateChanged += (_, _) => PostToUi(UpdateRunState);
        }

        UpdateRunState();
    }

    /// <summary>Gets the Look panel.</summary>
    public LookPanelViewModel Look { get; } = new();

    /// <summary>Gets the presets.</summary>
    public IReadOnlyList<LookPreset> Presets { get; } = LookPresets.All;

    /// <summary>Gets the sample-count choices.</summary>
    public IReadOnlyList<int> SampleCountOptions { get; } = [5, 10, 20, 30];

    /// <summary>Gets the channel modes.</summary>
    public IReadOnlyList<ChannelMode> ChannelModes { get; } = Enum.GetValues<ChannelMode>();

    /// <summary>Gets the group names of the project's rules.</summary>
    public ObservableCollection<string> Groups { get; } = [];

    /// <summary>Gets the installed model names.</summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>Gets the thumbnail strip.</summary>
    public ObservableCollection<CompareSampleViewModel> Samples { get; } = [];

    /// <summary>Gets the finished-job search results.</summary>
    public ObservableCollection<CompareSampleViewModel> SearchResults { get; } = [];

    /// <summary>Gets or sets the selected group.</summary>
    [ObservableProperty]
    public partial string? SelectedGroup { get; set; }

    /// <summary>Gets or sets the model being previewed (the group's model unless the user picks another).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelDifferentFromGroup))]
    public partial string? SelectedModel { get; set; }

    /// <summary>Gets or sets the selected group's model in the rules.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelDifferentFromGroup))]
    public partial string? GroupModel { get; set; }

    /// <summary>Gets or sets how many random samples to preview.</summary>
    [ObservableProperty]
    public partial int SampleCount { get; set; } = DefaultSampleCount;

    /// <summary>Gets or sets the finished-job search text.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Gets or sets the sample being compared.</summary>
    [ObservableProperty]
    public partial CompareSampleViewModel? SelectedSample { get; set; }

    /// <summary>Gets or sets the strip selection; picking one compares it.</summary>
    [ObservableProperty]
    public partial CompareSampleViewModel? SelectedStripSample { get; set; }

    /// <summary>Gets or sets the search-result selection; picking one switches to its group and compares it.</summary>
    [ObservableProperty]
    public partial CompareSampleViewModel? SelectedSearchResult { get; set; }

    /// <summary>Gets or sets the "before" image: the source, shown nearest-upscaled to the target size.</summary>
    [ObservableProperty]
    public partial RgbaImage? BeforeImage { get; set; }

    /// <summary>Gets or sets the "after" image: the rendered look (a draft while dragging).</summary>
    [ObservableProperty]
    public partial RgbaImage? AfterImage { get; set; }

    /// <summary>Gets or sets the channel view.</summary>
    [ObservableProperty]
    public partial ChannelMode Channel { get; set; }

    /// <summary>Gets or sets the split position, 0 (all after) to 1 (all before).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitOffset))]
    public partial double Split { get; set; } = 0.5;

    /// <summary>Gets or sets whether the image is fitted to the viewport (otherwise <see cref="Zoom"/> applies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial bool IsFitToView { get; set; } = true;

    /// <summary>Gets or sets the zoom in percent of actual pixels (100–400).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial int Zoom { get; set; } = MinZoom;

    /// <summary>Gets or sets the horizontal pan in screen pixels (shared by both sides).</summary>
    [ObservableProperty]
    public partial double PanX { get; set; }

    /// <summary>Gets or sets the vertical pan in screen pixels (shared by both sides).</summary>
    [ObservableProperty]
    public partial double PanY { get; set; }

    /// <summary>Gets or sets the viewport width (set by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial double ViewportWidth { get; set; }

    /// <summary>Gets or sets the viewport height (set by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial double ViewportHeight { get; set; }

    /// <summary>Gets or sets the target width of the sample being compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial int TargetWidth { get; set; }

    /// <summary>Gets or sets the target height of the sample being compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayScale), nameof(DisplayWidth), nameof(DisplayHeight), nameof(SplitOffset), nameof(ZoomText))]
    public partial int TargetHeight { get; set; }

    /// <summary>Gets or sets the error shown in the banner, or <see langword="null"/> when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Gets or sets whether a pipeline run is active (any state but Idle).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGpuAvailable))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(UseModelForGroupCommand))]
    public partial bool IsRunActive { get; set; }

    /// <summary>Gets whether an error is shown.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>Gets the run-lock banner text.</summary>
    public string RunLockReason { get; } = RunActiveReason;

    /// <summary>Gets or sets the status line.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Pick a group and preview samples, or search for a finished texture.";

    /// <summary>Gets or sets whether a sample is being built (decode and upscale).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Gets or sets the upscale progress, 0–1.</summary>
    [ObservableProperty]
    public partial double LoadProgress { get; set; }

    /// <summary>Gets or sets whether the sliders differ from the shown render (the preview is out of date until Render).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand))]
    public partial bool IsRenderPending { get; set; }

    /// <summary>Gets whether Undo has a render to go back to.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets the undo depth (for tests and diagnostics).</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Gets whether the previewed model differs from the group's model.</summary>
    public bool IsModelDifferentFromGroup => SelectedModel is not null && !string.Equals(SelectedModel, GroupModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether GPU upscaling is available now (no run active).</summary>
    public bool IsGpuAvailable => !IsRunActive && _engine.IsUpscalingAvailable;

    /// <summary>Gets the screen pixels per texture pixel.</summary>
    public double DisplayScale
    {
        get
        {
            if (!IsFitToView)
            {
                return Zoom / 100.0;
            }

            if (TargetWidth <= 0 || TargetHeight <= 0 || ViewportWidth <= 0 || ViewportHeight <= 0)
            {
                return 1.0;
            }

            return Math.Min(ViewportWidth / TargetWidth, ViewportHeight / TargetHeight);
        }
    }

    /// <summary>Gets the displayed image width.</summary>
    public double DisplayWidth => TargetWidth * DisplayScale;

    /// <summary>Gets the displayed image height.</summary>
    public double DisplayHeight => TargetHeight * DisplayScale;

    /// <summary>Gets the split line's offset from the image's left edge, in screen pixels.</summary>
    public double SplitOffset => DisplayWidth * Split;

    /// <summary>Gets the zoom label.</summary>
    public string ZoomText => IsFitToView ? $"Fit ({DisplayScale * 100:F0}%)" : $"{Zoom}%";

    /// <summary>
    /// Picks <paramref name="count"/> distinct jobs at random (a partial Fisher–Yates shuffle over the jobs in order). The same
    /// <paramref name="random"/> seed and jobs always give the same picks.
    /// </summary>
    /// <param name="jobs">The candidates.</param>
    /// <param name="count">How many to pick.</param>
    /// <param name="random">The random source.</param>
    /// <returns>The picks, in pick order.</returns>
    public static IReadOnlyList<JobRecord> PickRandom(IReadOnlyList<JobRecord> jobs, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var pool = jobs.ToArray();
        var picks = Math.Min(count, pool.Length);
        for (var i = 0; i < picks; i++)
        {
            var j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool[..picks];
    }

    /// <summary>Loads the groups and models of the open project.</summary>
    /// <returns>A task that completes when loaded.</returns>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        Models.Clear();
        foreach (var model in _engine.Models)
        {
            Models.Add(model.Name);
        }

        Groups.Clear();
        _rules = null;
        if (!_data.HasProject)
        {
            Status = PreviewEngine.NoProjectReason;
            return;
        }

        try
        {
            _rules = await _data.LoadRulesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ReportError("Could not load the project's rules", ex);
            return;
        }

        foreach (var group in _rules.Groups)
        {
            Groups.Add(group.Name);
        }

        if (SelectedGroup is null || !Groups.Contains(SelectedGroup))
        {
            SelectedGroup = Groups.FirstOrDefault();
        }
        else
        {
            LoadGroupModel();
        }

        await LoadLayerAsync(LookScope.All);
        await LoadLayerAsync(LookScope.Group);
        UpdateRunState();
    }

    /// <summary>Fills the strip with <see cref="SampleCount"/> random jobs of the selected group.</summary>
    /// <returns>A task that completes when the strip is filled (thumbnails load in the background).</returns>
    [RelayCommand]
    public async Task PreviewRandomAsync()
    {
        if (SelectedGroup is not { } group || !_data.HasProject)
        {
            Status = _data.HasProject ? "Pick a group first." : PreviewEngine.NoProjectReason;
            return;
        }

        IReadOnlyList<JobRecord> jobs;
        try
        {
            jobs = await _data.GetGroupJobsAsync(group, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ReportError($"Could not read the jobs of {group}", ex);
            return;
        }

        var picks = PickRandom(jobs, Math.Max(1, SampleCount), _random);
        Samples.Clear();
        foreach (var job in picks)
        {
            Samples.Add(new CompareSampleViewModel(job));
        }

        Status = picks.Count == 0 ? $"{group} has no textures to preview." : $"{picks.Count} random samples from {group}. Pick one to compare.";
        SelectedStripSample = Samples.FirstOrDefault();
        await LoadThumbnailsAsync([.. Samples]);
    }

    /// <summary>Searches finished jobs by path.</summary>
    /// <returns>A task that completes when the results are shown.</returns>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (!_data.HasProject)
        {
            Status = PreviewEngine.NoProjectReason;
            return;
        }

        IReadOnlyList<JobRecord> found;
        try
        {
            found = await _data.SearchFinishedAsync(SearchText, SearchLimit, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ReportError("Search failed", ex);
            return;
        }

        SearchResults.Clear();
        foreach (var job in found)
        {
            SearchResults.Add(new CompareSampleViewModel(job));
        }

        Status = found.Count == 0 ? "No finished texture matches." : $"{found.Count} finished textures match.";
    }

    /// <summary>Applies a preset to the Look panel.</summary>
    /// <param name="preset">The preset.</param>
    [RelayCommand]
    public void ApplyPreset(LookPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        Look.ApplyPreset(preset.Look);
        Status = $"Preset {preset.Name} set at {ScopeName(Look.Scope)}: {preset.Description}. Render to see it.";
    }

    /// <summary>Gets the confirmation text for <paramref name="affected"/> finished textures.</summary>
    /// <param name="affected">The affected finished jobs.</param>
    /// <returns>The question, with the first file names.</returns>
    public static string ApplyToFinishedMessage(IReadOnlyList<JobRecord> affected)
    {
        ArgumentNullException.ThrowIfNull(affected);
        var names = affected.Take(AffectedListLimit).Select(job => $"  • {job.Path}");
        var more = affected.Count > AffectedListLimit ? $"\n  …and {affected.Count - AffectedListLimit} more" : string.Empty;
        return $"{ApplyToFinishedQuestion} {affected.Count} finished textures?\n\n{string.Join('\n', names)}{more}\n\nYes re-queues them with the new look. No keeps them; the look applies to textures forged from now on.";
    }

    /// <summary>
    /// Saves the edited scope's look (the rules for All and Group, the per-texture override for This texture), then lists the affected
    /// finished textures and asks before re-queueing them (default No).
    /// </summary>
    /// <returns>A task that completes when done.</returns>
    [RelayCommand(CanExecute = nameof(CanApply))]
    public async Task ApplyAsync()
    {
        UpdateRunState();
        if (IsRunActive)
        {
            ShowError(RunActiveReason);
            return;
        }

        if (!_data.HasProject)
        {
            Status = PreviewEngine.NoProjectReason;
            return;
        }

        var scope = Look.Scope;
        var group = SelectedGroup;
        var path = SelectedSample?.Job.Path;
        if (scope == LookScope.Group && group is null)
        {
            ShowError("Pick a group before applying a group look.");
            return;
        }

        if (scope == LookScope.Texture && path is null)
        {
            ShowError("Pick a texture before applying a texture look.");
            return;
        }

        var target = ScopeName(scope);
        try
        {
            await _looks.SaveAsync(scope, group, path, Look.CurrentOverrides, CancellationToken.None);
            Look.MarkSaved(scope);
            if (scope == LookScope.Group)
            {
                _rules = await _data.LoadRulesAsync(CancellationToken.None);
            }

            var affected = await _looks.FindAffectedFinishedAsync(scope, group, path, CancellationToken.None);
            if (affected.Count == 0)
            {
                Status = $"Look saved for {target}.";
                return;
            }

            if (!await _dialogs.ConfirmAsync("Apply look", ApplyToFinishedMessage(affected)))
            {
                Status = $"Look saved for {target}. Finished textures were kept.";
                return;
            }

            var requeued = await _data.RequeueDoneAsync([.. affected.Select(job => job.Id)], CancellationToken.None);
            Status = $"Look saved for {target}; {requeued} finished textures re-queued.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            ReportError($"Could not apply the look to {target}", ex);
        }
    }

    /// <summary>Discards the edited scope's unsaved changes.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    public void Revert() => Look.Revert();

    /// <summary>
    /// Renders the current slider values onto the preview from the cached upscale. The previous values and their render go onto the
    /// undo stack.
    /// </summary>
    /// <returns>A task that completes when the render is shown or superseded.</returns>
    [RelayCommand(CanExecute = nameof(IsRenderPending))]
    public Task RenderAsync() => RenderLookAsync(pushUndo: true);

    /// <summary>Restores the slider values from before the last Render and shows that earlier render again (no re-render).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (!_undo.TryPop(out var entry))
        {
            return;
        }

        _render = entry.Render;
        _renderedOverrides = entry.Overrides;
        Look.RestoreOverrides(entry.Overrides);
        UpdateImages();
        UpdateRenderState();
        RaiseUndoChanged();
        Status = "Undid the last render.";
    }

    /// <summary>Makes the previewed model the selected group's model.</summary>
    /// <returns>A task that completes when the rules are stored.</returns>
    [RelayCommand(CanExecute = nameof(CanChangeRules))]
    public async Task UseModelForGroupAsync()
    {
        UpdateRunState();
        if (IsRunActive)
        {
            ShowError(RunActiveReason);
            return;
        }

        if (SelectedGroup is not { } groupName || SelectedModel is not { } model || !_data.HasProject)
        {
            return;
        }

        try
        {
            var rules = await _data.LoadRulesAsync(CancellationToken.None);
            if (!TryReplaceGroup(rules, groupName, group => group with { Model = model }, out var updated))
            {
                Status = $"Group {groupName} is no longer in the rules.";
                return;
            }

            await _data.SaveRulesAsync(updated, CancellationToken.None);
            _rules = updated;
            GroupModel = model;
            Status = $"{groupName} now uses {model}. Rescan to update queued jobs.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ReportError($"Could not change the model of {groupName}", ex);
        }
    }

    /// <summary>Hides the error banner.</summary>
    [RelayCommand]
    public void DismissError() => ErrorMessage = null;

    /// <summary>Re-reads the runner state (also called on every run start and end).</summary>
    public void UpdateRunState() => IsRunActive = (_runner?.State ?? PipelineState.Idle) != PipelineState.Idle || !_engine.IsUpscalingAvailable;

    /// <summary>Zooms in one step (leaves fit mode).</summary>
    [RelayCommand]
    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(step => step > CurrentZoomPercent(), MaxZoom));

    /// <summary>Zooms out one step; below 100 % it returns to fit mode.</summary>
    [RelayCommand]
    public void ZoomOut()
    {
        var current = CurrentZoomPercent();
        if (current <= MinZoom)
        {
            FitToView();
            return;
        }

        SetZoom(ZoomSteps.LastOrDefault(step => step < current, MinZoom));
    }

    /// <summary>Fits the image to the viewport and centres it.</summary>
    [RelayCommand]
    public void FitToView()
    {
        IsFitToView = true;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>Sets the zoom (clamped to 100–400 %) and leaves fit mode; the pan is re-clamped.</summary>
    /// <param name="percent">The zoom in percent of actual pixels.</param>
    public void SetZoom(int percent)
    {
        Zoom = Math.Clamp(percent, MinZoom, MaxZoom);
        IsFitToView = false;
        ClampPan();
    }

    /// <summary>Pans both sides together by a screen-pixel delta, clamped so the image never leaves the viewport.</summary>
    /// <param name="deltaX">Horizontal delta.</param>
    /// <param name="deltaY">Vertical delta.</param>
    public void PanBy(double deltaX, double deltaY)
    {
        PanX += deltaX;
        PanY += deltaY;
        ClampPan();
    }

    /// <summary>Moves the split line to a screen x offset from the image's left edge.</summary>
    /// <param name="offset">The offset in screen pixels.</param>
    public void SetSplitFromOffset(double offset) => Split = DisplayWidth <= 0 ? 0.5 : Math.Clamp(offset / DisplayWidth, 0.0, 1.0);

    partial void OnSelectedGroupChanged(string? value)
    {
        SelectedStripSample = null;
        Samples.Clear();
        LoadGroupModel();
        _ = LoadLayerAsync(LookScope.Group);
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (SelectedSample is not null)
        {
            _ = LoadSelectedAsync();
        }
    }

    partial void OnSelectedSampleChanged(CompareSampleViewModel? value)
    {
        if (value is not null)
        {
            Look.Kind = value.Kind;
            Look.UsesModel = value.ToRequest(SelectedModel).UsesModel;
        }

        _ = LoadSelectedAsync();
    }

    partial void OnSelectedStripSampleChanged(CompareSampleViewModel? value)
    {
        if (value is not null)
        {
            SelectedSample = value;
        }
    }

    partial void OnSelectedSearchResultChanged(CompareSampleViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Job.Group is { } group && Groups.Contains(group) && !string.Equals(group, SelectedGroup, StringComparison.Ordinal))
        {
            SelectedGroup = group;
        }

        SelectedSample = value;
    }

    partial void OnChannelChanged(ChannelMode value) => UpdateImages();

    partial void OnSplitChanged(double value)
    {
        if (value is < 0.0 or > 1.0)
        {
            Split = Math.Clamp(value, 0.0, 1.0);
        }
    }

    partial void OnViewportWidthChanged(double value) => ClampPan();

    partial void OnViewportHeightChanged(double value) => ClampPan();

    private static bool TryReplaceGroup(ForgeRules rules, string groupName, Func<GroupRule, GroupRule> change, out ForgeRules updated)
    {
        var groups = rules.Groups.ToList();
        var index = groups.FindIndex(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            updated = rules;
            return false;
        }

        groups[index] = change(groups[index]);
        updated = rules with { Groups = groups };
        return true;
    }

    private static RgbaImage MakeThumbnail(RgbaImage source)
    {
        if (Math.Max(source.Width, source.Height) <= ThumbnailSide)
        {
            return source;
        }

        var (width, height) = PreviewSample.FitToMax(source.Width, source.Height, ThumbnailSide);
        var rgb = ImageOps.Resize(ImageOps.ToPlanarRgb(source), width, height, ResizeFilter.Bicubic);
        return ImageOps.ToRgba(rgb, null);
    }

    private int CurrentZoomPercent() => (int)Math.Round(DisplayScale * 100);

    private void ClampPan()
    {
        var maxX = Math.Max(0, (DisplayWidth - ViewportWidth) / 2);
        var maxY = Math.Max(0, (DisplayHeight - ViewportHeight) / 2);
        PanX = Math.Clamp(PanX, -maxX, maxX);
        PanY = Math.Clamp(PanY, -maxY, maxY);
    }

    private static string ScopeName(LookScope scope) => scope switch
    {
        LookScope.All => "All",
        LookScope.Group => "this group",
        _ => "this texture",
    };

    private void LoadGroupModel()
    {
        var group = _rules?.Groups.FirstOrDefault(rule => string.Equals(rule.Name, SelectedGroup, StringComparison.OrdinalIgnoreCase));
        GroupModel = group?.Model;
        SelectedModel = group?.Model;
    }

    private async Task LoadLayerAsync(LookScope scope)
    {
        if (!_data.HasProject)
        {
            return;
        }

        try
        {
            var overrides = await _looks.GetAsync(scope, SelectedGroup, SelectedSample?.Job.Path, CancellationToken.None);
            Look.LoadLayer(scope, overrides);
            if (scope == Look.Scope)
            {
                ClearUndo();
            }

            UpdateRenderState();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            ReportError($"Could not load the {ScopeName(scope)} look", ex);
        }
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<CompareSampleViewModel> items)
    {
        foreach (var item in items)
        {
            try
            {
                var source = await _engine.GetSourceAsync(item.Job.Path, item.Job.Archive, CancellationToken.None);
                item.Thumbnail = await Task.Run(() => MakeThumbnail(source));
                item.IsCached = _engine.IsCached(item.ToRequest(SelectedModel));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TexconvException)
            {
                LogThumbnailFailed(ex, item.Job.Path);
            }
        }
    }

    private async Task LoadSelectedAsync()
    {
        _loadCancellation?.Cancel();
        _engine.CancelRenders();
        _sample = null;
        _render = null;
        ClearUndo();
        if (SelectedSample is not { } item)
        {
            Look.LoadLayer(LookScope.Texture, LookOverrides.Empty);
            UpdateImages();
            UpdateRenderState();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var request = item.ToRequest(SelectedModel);
        (TargetWidth, TargetHeight) = (request.TargetWidth, request.TargetHeight);
        Look.Kind = item.Kind;
        Look.UsesModel = request.UsesModel;
        IsLoading = true;
        LoadProgress = 0;
        await LoadLayerAsync(LookScope.Texture);
        Status = request.UsesModel && !_engine.IsCached(request)
            ? $"Upscaling {item.Name} with {request.Model}…"
            : $"Loading {item.Name}…";
        try
        {
            var progress = new Progress<TileProgress>(tile => LoadProgress = tile.Total == 0 ? 0 : (double)tile.Done / tile.Total);
            var result = await _engine.GetSampleAsync(request, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            UpdateRunState();
            if (result.Sample is not { } sample)
            {
                BeforeImage = null;
                AfterImage = null;
                var reason = result.UnavailableReason ?? "Preview not available.";
                if (reason == PreviewEngine.NoProjectReason)
                {
                    Status = reason;
                }
                else
                {
                    // GPU busy (a run or another GPU job holds it): say who, in the banner, and render nothing.
                    ShowError(reason);
                }

                return;
            }

            _sample = sample;
            item.IsCached = true;
            item.Thumbnail ??= MakeThumbnail(sample.Source);
            UpdateImages();
            Status = $"{item.Name} · {item.SizeText} · {(sample.UsedModel ? request.Model : "Lanczos only")}";
            await RenderLookAsync(pushUndo: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer selection replaced this load; it reports its own status.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TexconvException or KeyNotFoundException)
        {
            ReportError($"Could not preview {item.Name}", ex);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    private async Task RenderLookAsync(bool pushUndo)
    {
        if (_sample is not { } sample)
        {
            return;
        }

        var previous = _render;
        var previousOverrides = _renderedOverrides;
        var overrides = Look.CurrentOverrides;
        try
        {
            var outcome = await _engine.SubmitRenderAsync(sample, Look.ToSettings(), draft: false);
            if (!outcome.IsCurrent || !ReferenceEquals(_sample, sample) || outcome.Result is not { } render)
            {
                return;
            }

            if (pushUndo && previous is not null && previousOverrides is not null)
            {
                _undo.Push((previousOverrides, previous));
                RaiseUndoChanged();
            }

            _render = render;
            _renderedOverrides = overrides;
            UpdateImages();
            UpdateRenderState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            ReportError("Rendering the look failed", ex);
        }
    }

    private void UpdateRenderState() => IsRenderPending = _sample is not null && (_render is null || !_render.Look.Equals(Look.ToSettings()));

    private void ClearUndo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _undo.Clear();
        RaiseUndoChanged();
    }

    private void RaiseUndoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoDepth));
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void UpdateImages()
    {
        BeforeImage = _sample is null ? null : ChannelView.Convert(_sample.Source, Channel);
        AfterImage = _render is null ? null : ChannelView.Convert(_render.Image, Channel);
    }

    private bool CanChangeRules() => !IsRunActive;

    private bool CanApply() => !IsRunActive && Look.IsScopeDirty;

    private bool CanRevert() => Look.IsScopeDirty;

    private void PostToUi(Action action)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        Status = message;
    }

    private void ReportError(string what, Exception exception)
    {
        LogError(exception, what);
        ShowError($"{what}: {exception.Message}");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{What}")]
    private partial void LogError(Exception exception, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Thumbnail failed for {Path}")]
    private partial void LogThumbnailFailed(Exception exception, string path);
}
