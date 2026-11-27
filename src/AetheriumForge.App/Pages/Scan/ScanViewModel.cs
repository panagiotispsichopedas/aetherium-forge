using System.Collections.ObjectModel;
using System.Globalization;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>
/// The Scan page (IMPLEMENTATION_GUIDE §4, §14 "Scan"): detected installs with a validated manual override, the profile files, drives and the
/// project folder, an async cancellable scan that imports into the project, and the summary cards. Errors show inline and never crash the app.
/// File-system and database work runs off the UI thread.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject
{
    private static readonly ScanPhase[] Phases = Enum.GetValues<ScanPhase>();

    private readonly IScanEnvironment _environment;
    private readonly IScanProjectSession _session;
    private readonly INavigationService _navigation;
    private readonly IFolderPicker _folderPicker;
    private readonly IForgeRunner? _runner;
    private readonly ILogger<ScanViewModel> _logger;
    private readonly Action<Action> _postToUi;
    private IReadOnlyList<(string Root, long Free, bool IsSystem)> _drives = [];
    private string? _projectFolderOverride;
    private bool _projectFolderIsDefault;
    private string? _summaryRoot;
    private CancellationTokenSource? _scanCancellation;
    private int _profileVersion;
    private int _folderVersion;

    /// <summary>Initializes a new instance of the <see cref="ScanViewModel"/> class.</summary>
    /// <param name="environment">Discovery and scan.</param>
    /// <param name="session">The project session.</param>
    /// <param name="navigation">Navigation (the Go to Queue link).</param>
    /// <param name="folderPicker">Folder dialogs.</param>
    /// <param name="runner">The pipeline runner, when registered: Scan, Rescan and Open are blocked while a run is active.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <param name="postToUi">Runs an action on the UI thread; <see langword="null"/> uses <see cref="Dispatcher.UIThread"/>. Tests pass an inline runner.</param>
    public ScanViewModel(
        IScanEnvironment environment,
        IScanProjectSession session,
        INavigationService navigation,
        IFolderPicker folderPicker,
        IForgeRunner? runner = null,
        ILogger<ScanViewModel>? logger = null,
        Action<Action>? postToUi = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(folderPicker);

        _environment = environment;
        _session = session;
        _navigation = navigation;
        _folderPicker = folderPicker;
        _runner = runner;
        _logger = logger ?? NullLogger<ScanViewModel>.Instance;
        _postToUi = postToUi ?? (action => Dispatcher.UIThread.Post(action));
        _session.Changed += OnSessionChanged;
        if (_runner is not null)
        {
            _runner.RunStateChanged += (_, _) => _postToUi(OnRunStateChanged);
        }
    }

    /// <summary>Gets the detected installs plus any folder the user browsed to.</summary>
    public ObservableCollection<GameInstallItem> Installs { get; } = [];

    /// <summary>Gets the fixed drives with their free space.</summary>
    public ObservableCollection<DriveItem> Drives { get; } = [];

    /// <summary>Gets the recently opened projects.</summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>Gets a value indicating whether <see cref="InitializeAsync"/> has run.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Gets a value indicating whether a pipeline run is active (running, paused or stopping).</summary>
    public bool IsRunActive => _runner?.State is PipelineState.Running or PipelineState.Paused or PipelineState.Stopping;

    /// <summary>Gets a value indicating whether the page can start work (no scan and no pipeline run).</summary>
    public bool IsIdle => !IsScanning && !IsRunActive;

    /// <summary>Gets the background profile load started by the last install selection (for tests).</summary>
    internal Task ProfileLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the background project-folder refresh started by the last change (for tests).</summary>
    internal Task ProjectFolderLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the background summary load started by the last project change (for tests).</summary>
    internal Task SummaryLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Gets or sets the selected install.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private GameInstallItem? _selectedInstall;

    /// <summary>Gets or sets the open project's game folder when it is not a detected or valid install, else <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnresolvedGame))]
    private string? _unresolvedGameFolder;

    /// <summary>Gets or sets a value indicating whether installs are being detected.</summary>
    [ObservableProperty]
    private bool _isDetecting;

    /// <summary>Gets or sets the INI folder of the selected install's profile.</summary>
    [ObservableProperty]
    private string? _iniFolder;

    /// <summary>Gets or sets the <c>plugins.txt</c> path of the selected install's profile.</summary>
    [ObservableProperty]
    private string? _pluginsTxtPath;

    /// <summary>Gets or sets a note on <c>plugins.txt</c> (found, or the fallback when missing).</summary>
    [ObservableProperty]
    private string? _pluginsTxtNote;

    /// <summary>Gets or sets the load order summary after the profile loaded, for example <c>254 plugins, 18 INI archives, 40 plugin archives</c>.</summary>
    [ObservableProperty]
    private string? _loadOrderText;

    /// <summary>Gets or sets the project folder the scan imports into.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string? _projectFolder;

    /// <summary>Gets or sets why that folder was chosen (existing project, best drive, chosen folder, open project).</summary>
    [ObservableProperty]
    private string? _projectFolderReason;

    /// <summary>Gets or sets the open project's folder, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToQueueCommand))]
    private string? _openProjectRoot;

    /// <summary>Gets or sets a value indicating whether a scan runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExistingCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseProjectFolderCommand))]
    private bool _isScanning;

    /// <summary>Gets or sets the scan progress, 0 to 100.</summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>Gets or sets the scan progress text.</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>Gets or sets the summary cards, or <see langword="null"/> before a scan or project open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private ScanSummary? _summary;

    /// <summary>Gets or sets the inline error, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>Gets or sets the inline warning (for example archives the scan could not read), or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warningMessage;

    /// <summary>Gets or sets the inline information message, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _infoMessage;

    /// <summary>Gets a value indicating whether a project is open.</summary>
    public bool HasProject => OpenProjectRoot is not null;

    /// <summary>Gets a value indicating whether the open project's game could not be resolved.</summary>
    public bool HasUnresolvedGame => UnresolvedGameFolder is not null;

    /// <summary>Gets a value indicating whether there is a summary to show.</summary>
    public bool HasSummary => Summary is not null;

    /// <summary>Gets a value indicating whether an error is shown.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>Gets a value indicating whether a warning is shown.</summary>
    public bool HasWarning => WarningMessage is not null;

    /// <summary>Gets a value indicating whether an information message is shown.</summary>
    public bool HasInfo => InfoMessage is not null;

    /// <summary>Detects the installs and drives and shows the open project, if any. The view runs it once when it first loads.</summary>
    /// <returns>A task that completes when detection finished.</returns>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        IsInitialized = true;
        await RunGuardedAsync("Detecting the game failed", async () =>
        {
            IsDetecting = true;
            try
            {
                _drives = await Task.Run(_environment.GetFixedDrives);
                var installs = await _environment.FindInstallsAsync(CancellationToken.None);
                foreach (var install in installs)
                {
                    Installs.Add(new GameInstallItem(install));
                }

                ShowProjectState();
                SelectedInstall = FindInstall(_session.Current?.GameFolder) ?? Installs.FirstOrDefault();
                await ProjectFolderLoad;
                if (Installs.Count == 0)
                {
                    ErrorMessage = "Skyrim Special Edition was not found (Steam, GOG, Epic, registry). Use Browse… to choose the game folder.";
                }
            }
            finally
            {
                IsDetecting = false;
            }
        });
    }

    /// <summary>Lets the user choose the game folder by hand; the folder is validated before it is used.</summary>
    /// <returns>A task that completes when the folder was handled.</returns>
    [RelayCommand(CanExecute = nameof(CanPickFolders))]
    private Task BrowseGameAsync() => RunGuardedAsync("Choosing the game folder failed", async () =>
    {
        var folder = await _folderPicker.PickFolderAsync("Choose the Skyrim Special Edition folder (the one with SkyrimSE.exe)", SelectedInstall?.GameFolder);
        if (folder is not null)
        {
            await UseGameFolderAsync(folder);
        }
    });

    /// <summary>Lets the user choose the project folder instead of the default one.</summary>
    /// <returns>A task that completes when the folder was handled.</returns>
    [RelayCommand(CanExecute = nameof(CanPickFolders))]
    private Task ChooseProjectFolderAsync() => RunGuardedAsync("Choosing the project folder failed", async () =>
    {
        var folder = await _folderPicker.PickFolderAsync("Choose the project folder (outside the game folder)", ProjectFolder);
        if (folder is not null)
        {
            await UseProjectFolderAsync(folder);
        }
    });

    /// <summary>Scans the selected install into the project folder.</summary>
    /// <returns>A task that completes when the scan ended (also on cancel or error).</returns>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => SelectedInstall is { } install && ProjectFolder is { } folder
        ? RunScanAsync(install.Install, folder, _projectFolderIsDefault)
        : Task.CompletedTask;

    /// <summary>Scans the open project's game again into the open project.</summary>
    /// <returns>A task that completes when the scan ended.</returns>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        if (_session.Current is not { } project)
        {
            return;
        }

        GameInstall? game = null;
        await RunGuardedAsync("Rescanning failed", async () =>
        {
            game = await ResolveProjectGameAsync(project);
            if (game is null)
            {
                UnresolvedGameFolder = project.GameFolder;
                ErrorMessage = $"The project's game folder {project.GameFolder} is no longer a Skyrim Special Edition folder.";
            }
        });

        if (game is not null)
        {
            await RunScanAsync(game, project.Root, isDefaultFolder: false);
        }
    }

    /// <summary>Cancels the running scan.</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _scanCancellation?.Cancel();

    /// <summary>Opens an existing project folder chosen by the user.</summary>
    /// <returns>A task that completes when the project is open (or the error shown).</returns>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task OpenExistingAsync() => RunGuardedAsync("Opening the project failed", async () =>
    {
        var folder = await _folderPicker.PickFolderAsync("Open an Aetherium Forge project (the folder with forge.db)", OpenProjectRoot);
        if (folder is not null)
        {
            await OpenProjectAsync(folder);
        }
    });

    /// <summary>Opens a recent project.</summary>
    /// <param name="folder">The project folder.</param>
    /// <returns>A task that completes when the project is open (or the error shown).</returns>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task OpenRecentAsync(string? folder) => string.IsNullOrWhiteSpace(folder)
        ? Task.CompletedTask
        : RunGuardedAsync("Opening the project failed", () => OpenProjectAsync(folder));

    /// <summary>Shows the Queue page.</summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void GoToQueue() => _navigation.NavigateTo(AppPage.Queue);

    /// <summary>Hides the inline error.</summary>
    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    /// <summary>Hides the inline warning.</summary>
    [RelayCommand]
    private void DismissWarning() => WarningMessage = null;

    /// <summary>Hides the inline information message.</summary>
    [RelayCommand]
    private void DismissInfo() => InfoMessage = null;

    private bool CanScan() => IsIdle && SelectedInstall is not null && ProjectFolder is not null;

    private bool CanRescan() => IsIdle && HasProject;

    private bool CanPickFolders() => !IsScanning;

    partial void OnSelectedInstallChanged(GameInstallItem? value)
    {
        if (value is not null)
        {
            UnresolvedGameFolder = null;
        }

        ProjectFolderLoad = RefreshProjectFolderAsync();
        ProfileLoad = LoadProfileInfoAsync(value?.Install);
    }

    /// <summary>Validates a game folder and selects it (the manual override).</summary>
    /// <param name="folder">The folder.</param>
    /// <returns><see langword="true"/> when the folder is a valid install.</returns>
    internal async Task<bool> UseGameFolderAsync(string folder)
    {
        var install = await Task.Run(() => _environment.ValidateInstall(folder, GameStore.Manual));
        if (install is null)
        {
            ErrorMessage = $"{folder} is not a Skyrim Special Edition folder (no {GameLocator.ExecutableName} and Data folder).";
            return false;
        }

        ErrorMessage = null;
        SelectedInstall = FindInstall(install.GameFolder) ?? AddInstall(install);
        await ProjectFolderLoad;
        return true;
    }

    /// <summary>Uses a project folder chosen by the user; rejects a drive root and any folder inside a known game folder.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns><see langword="true"/> when the folder is accepted.</returns>
    internal async Task<bool> UseProjectFolderAsync(string folder)
    {
        var root = ProjectLayout.For(folder).Root;
        if (IsDriveRoot(root))
        {
            ErrorMessage = $"{root} is a drive root. Choose or create a folder on it for the project.";
            return false;
        }

        var games = Installs.Select(i => i.GameFolder).ToList();
        var inside = await Task.Run(() => games.FirstOrDefault(game => PathGuard.IsUnder(root, game)));
        if (inside is not null)
        {
            ErrorMessage = $"The project folder {root} is inside the game folder {inside}. Aetherium Forge never writes into the game; choose a folder outside it.";
            return false;
        }

        ErrorMessage = null;
        _projectFolderOverride = root;
        ProjectFolderLoad = RefreshProjectFolderAsync();
        await ProjectFolderLoad;
        return true;
    }

    private static bool IsDriveRoot(string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root) ?? string.Empty), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    private async Task RunScanAsync(GameInstall game, string projectFolder, bool isDefaultFolder)
    {
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        ErrorMessage = null;
        WarningMessage = null;
        InfoMessage = null;
        IsScanning = true;
        ProgressValue = 0;
        ProgressText = "Loading the profile…";
        var previousSummaryRoot = _summaryRoot;
        var ct = cancellation.Token;
        try
        {
            var paths = await Task.Run(() => _environment.LocateProfile(game.Store), ct);
            var profile = await Task.Run(() => _environment.LoadProfileAsync(game, paths, ct), ct);
            LoadOrderText = FormatLoadOrder(profile.LoadOrder.Count, profile.IniArchives.Count, profile.PluginArchives.Count);

            var rules = await Task.Run(() => LoadRulesAsync(projectFolder, ct), ct);
            var result = await _environment.ScanAsync(profile, rules, new UiProgress(this), ct);

            var queuedTargets = result.Queued.Select(t => Math.Max(t.Decision.TargetWidth, t.Decision.TargetHeight)).ToList();
            var outputBytes = DriveAdvisor.EstimateOutputBytes(queuedTargets);
            if (isDefaultFolder)
            {
                // Like the CLI: the default folder is chosen again now that the size of the output is known.
                var tempBytes = DriveAdvisor.EstimateTempBytes(2, queuedTargets.Count == 0 ? 0 : queuedTargets.Max());
                var (layout, reason) = await Task.Run(
                    () => ProjectLocator.ResolveForScan(null, game.GameFolder, _drives, outputBytes + tempBytes, _environment.FileExists),
                    ct);
                projectFolder = layout.Root;
                ProjectFolder = layout.Root;
                ProjectFolderReason = reason;
            }

            ProgressText = "Importing into the project…";
            _summaryRoot = ProjectLayout.For(projectFolder).Root;
            var request = new ScanImportRequest(projectFolder, game, rules, result);
            var imported = await Task.Run(() => _session.ApplyScanAsync(request, ct), ct);
            _session.NotifyJobsChanged();

            Summary = ScanSummary.FromScan(result, $"Scan of {game.GameFolder}, {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}");
            ProgressValue = 100;
            ProgressText = "Scan complete.";
            InfoMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"Imported {imported.Upserted:N0} rows into {imported.DatabasePath}; {imported.NowProvidedByMod:N0} job(s) now provided by a mod.");
            if (imported.UnreadableArchives.Count > 0)
            {
                WarningMessage = $"{imported.UnreadableArchives.Count} archive(s) are missing or unreadable: {string.Join(", ", imported.UnreadableArchives)}. "
                    + "Their textures are not in this scan; existing jobs from them were left unchanged. Check the game files and rescan.";
            }

            LogScanFinished(_logger, result.Textures.Count, queuedTargets.Count, projectFolder);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _summaryRoot = previousSummaryRoot;
            ProgressText = "Scan cancelled.";
            InfoMessage = "The scan was cancelled; the project was not changed.";
            LogScanCancelled(_logger);
        }
#pragma warning disable CA1031 // The page boundary: every failure is logged and shown inline, never thrown into the UI loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _summaryRoot = previousSummaryRoot;
            ProgressText = "Scan failed.";
            ErrorMessage = ex.Message;
            LogFailed(_logger, ex, "Scan failed");
        }
        finally
        {
            _scanCancellation = null;
            IsScanning = false;
        }
    }

    private async Task<ForgeRules> LoadRulesAsync(string projectFolder, CancellationToken ct)
    {
        var layout = ProjectLayout.For(projectFolder);
        if (_session.Current is { } open && string.Equals(open.Root, layout.Root, StringComparison.OrdinalIgnoreCase))
        {
            return open.Rules;
        }

        if (_environment.FileExists(layout.DatabasePath)
            && await _environment.ReadProjectValueAsync(layout.DatabasePath, ProjectKeys.RulesJson, ct).ConfigureAwait(false) is { } json)
        {
            return ForgeRules.FromJson(json);
        }

        return ForgeRules.Default;
    }

    private async Task OpenProjectAsync(string folder)
    {
        ErrorMessage = null;
        WarningMessage = null;
        InfoMessage = null;
        await Task.Run(() => _session.OpenAsync(folder, CancellationToken.None));
        if (_session.Current is not { } project)
        {
            return;
        }

        // Select the project's game, so a Scan goes back into the same project.
        _projectFolderOverride = null;
        var game = await ResolveProjectGameAsync(project);
        if (game is not null)
        {
            SelectedInstall = FindInstall(game.GameFolder) ?? AddInstall(game);
            await ProjectFolderLoad;
            InfoMessage = $"Opened {project.Root}.";
        }
        else
        {
            UnresolvedGameFolder = project.GameFolder;
            SelectedInstall = null;
            await ProjectFolderLoad;
            WarningMessage = $"Opened {project.Root}, but its game folder {project.GameFolder} is not a Skyrim Special Edition install on this PC. "
                + "Use Browse… to choose the game before scanning.";
        }
    }

    private async Task<GameInstall?> ResolveProjectGameAsync(ProjectInfo project)
    {
        if (FindInstall(project.GameFolder) is { } known)
        {
            return known.Install;
        }

        var storeText = await _session.GetProjectValueAsync(ProjectKeys.GameStore, CancellationToken.None);
        var store = Enum.TryParse<GameStore>(storeText, ignoreCase: true, out var parsed) ? parsed : GameStore.Manual;
        return await Task.Run(() => _environment.ValidateInstall(project.GameFolder, store));
    }

    private GameInstallItem AddInstall(GameInstall install)
    {
        var item = new GameInstallItem(install);
        Installs.Add(item);
        return item;
    }

    private GameInstallItem? FindInstall(string? gameFolder) => gameFolder is null
        ? null
        : Installs.FirstOrDefault(i => string.Equals(
            Path.TrimEndingDirectorySeparator(i.GameFolder),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder)),
            StringComparison.OrdinalIgnoreCase));

    private async Task RefreshProjectFolderAsync()
    {
        var version = ++_folderVersion;
        var install = SelectedInstall;
        var chosen = _projectFolderOverride;
        var open = _session.Current;
        var drives = _drives;
        if (install is null)
        {
            ApplyProjectFolder(version, open is not null && UnresolvedGameFolder is not null ? open.Root : null, open is not null && UnresolvedGameFolder is not null ? "open project" : null, false, drives);
            return;
        }

        try
        {
            var (folder, reason, isDefault) = await Task.Run(() => ChooseProjectFolder(install, chosen, open, drives));
            ApplyProjectFolder(version, folder, reason, isDefault, drives);
        }
        catch (ProjectException ex)
        {
            ApplyProjectFolder(version, null, null, false, drives);
            if (version == _folderVersion)
            {
                ErrorMessage = $"{ex.Message.Replace(" Pass --project <dir>.", string.Empty, StringComparison.Ordinal)} Use Choose folder… to pick one.";
            }
        }
    }

    private (string Folder, string Reason, bool IsDefault) ChooseProjectFolder(
        GameInstallItem install,
        string? chosen,
        ProjectInfo? open,
        IReadOnlyList<(string Root, long Free, bool IsSystem)> drives)
    {
        if (chosen is not null && !PathGuard.IsUnder(chosen, install.GameFolder))
        {
            return (chosen, "chosen folder", false);
        }

        if (open is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(open.GameFolder),
            Path.TrimEndingDirectorySeparator(install.GameFolder),
            StringComparison.OrdinalIgnoreCase))
        {
            return (open.Root, "open project", false);
        }

        var existing = ProjectLocator.FindExisting(install.GameFolder, drives, _environment.FileExists);
        var (layout, reason) = ProjectLocator.ResolveForScan(null, install.GameFolder, drives, 0, _environment.FileExists);
        return (layout.Root, reason, existing is null);
    }

    private void ApplyProjectFolder(int version, string? folder, string? reason, bool isDefault, IReadOnlyList<(string Root, long Free, bool IsSystem)> drives)
    {
        if (version != _folderVersion)
        {
            return;
        }

        ProjectFolder = folder;
        ProjectFolderReason = reason;
        _projectFolderIsDefault = isDefault;
        Drives.Clear();
        var projectRoot = folder is null ? null : Path.GetPathRoot(folder);
        foreach (var (root, free, isSystem) in drives.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase))
        {
            Drives.Add(new DriveItem(root, free, isSystem, string.Equals(root, projectRoot, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private async Task LoadProfileInfoAsync(GameInstall? install)
    {
        var version = ++_profileVersion;
        IniFolder = null;
        PluginsTxtPath = null;
        PluginsTxtNote = null;
        LoadOrderText = null;
        if (install is null)
        {
            return;
        }

        try
        {
            var (paths, pluginsFound, profile) = await Task.Run(async () =>
            {
                var located = _environment.LocateProfile(install.Store);
                var found = _environment.FileExists(located.PluginsTxtPath);
                var loaded = await _environment.LoadProfileAsync(install, located, CancellationToken.None).ConfigureAwait(false);
                return (located, found, loaded);
            });
            if (version == _profileVersion)
            {
                IniFolder = paths.IniFolder;
                PluginsTxtPath = paths.PluginsTxtPath;
                PluginsTxtNote = pluginsFound ? "found" : "not found: base game and Creation Club only";
                LoadOrderText = FormatLoadOrder(profile.LoadOrder.Count, profile.IniArchives.Count, profile.PluginArchives.Count);
            }
        }
#pragma warning disable CA1031 // A profile that cannot be read is shown inline; the scan reports the same error if the user starts it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == _profileVersion)
            {
                LoadOrderText = $"The profile could not be read: {ex.Message}";
            }

            LogFailed(_logger, ex, "Reading the profile failed");
        }
    }

    private async void OnSessionChanged(object? sender, EventArgs e)
    {
        try
        {
            ShowProjectState();
            if (_session.Current is { } project && !string.Equals(project.Root, _summaryRoot, StringComparison.OrdinalIgnoreCase))
            {
                _summaryRoot = project.Root;
                var load = LoadSummaryFromStoreAsync(project.Root);
                SummaryLoad = load;
                await load;
            }
        }
#pragma warning disable CA1031 // An event handler on the UI thread: a failure is logged and shown, never rethrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = ex.Message;
            LogFailed(_logger, ex, "Updating the Scan page after a project change failed");
        }
    }

    private void OnRunStateChanged()
    {
        OnPropertyChanged(nameof(IsRunActive));
        OnPropertyChanged(nameof(IsIdle));
        ScanCommand.NotifyCanExecuteChanged();
        RescanCommand.NotifyCanExecuteChanged();
        OpenExistingCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
    }

    private void ShowProjectState()
    {
        OpenProjectRoot = _session.Current?.Root;
        RecentProjects.Clear();
        foreach (var folder in _session.RecentProjects)
        {
            RecentProjects.Add(folder);
        }

        if (IsInitialized && !IsScanning)
        {
            ProjectFolderLoad = RefreshProjectFolderAsync();
        }
    }

    private async Task LoadSummaryFromStoreAsync(string root)
    {
        try
        {
            var jobs = await Task.Run(() => _session.GetJobsAsync(CancellationToken.None));
            var scannedAt = await _session.GetProjectValueAsync(ProjectKeys.ScannedAt, CancellationToken.None);
            var when = DateTimeOffset.TryParse(scannedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? $", scanned {parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
                : string.Empty;
            if (string.Equals(_session.Current?.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                Summary = ScanSummary.FromJobs(jobs, $"Project {root}{when}");
            }
        }
#pragma warning disable CA1031 // The page boundary: a summary that cannot be read is an inline error, not a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"The project's jobs could not be read: {ex.Message}";
            LogFailed(_logger, ex, "Reading the project summary failed");
        }
    }

    private async Task RunGuardedAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // The page boundary: every failure is logged and shown inline, never thrown into the UI loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = ex.Message;
            LogFailed(_logger, ex, what);
        }
    }

    private void ApplyProgress(ScanProgress value)
    {
        var index = Array.IndexOf(Phases, value.Phase);
        var fraction = value.Total > 0 ? Math.Clamp((double)value.Done / value.Total, 0, 1) : 0;
        ProgressValue = Math.Max(ProgressValue, (index + fraction) * 100 / Phases.Length);
        ProgressText = value.Total > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{PhaseText(value.Phase)}… {value.Done:N0} / {value.Total:N0}")
            : $"{PhaseText(value.Phase)}…";
    }

    private static string PhaseText(ScanPhase phase) => phase switch
    {
        ScanPhase.LooseFiles => "Listing loose textures",
        ScanPhase.ModArchives => "Reading mod archives",
        ScanPhase.BaseArchives => "Reading base-game archives",
        ScanPhase.Headers => "Reading texture headers and applying rules",
        _ => phase.ToString(),
    };

    private static string FormatLoadOrder(int plugins, int iniArchives, int pluginArchives) =>
        string.Create(CultureInfo.CurrentCulture, $"{plugins:N0} plugins, {iniArchives:N0} INI archives, {pluginArchives:N0} plugin archives");

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Scan finished: {Gaps} gaps, {Queued} queued, into {Project}")]
    private static partial void LogScanFinished(ILogger logger, int gaps, int queued, string project);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Scan cancelled by the user")]
    private static partial void LogScanCancelled(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "{What}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string what);

    /// <summary>Forwards scan progress to the UI thread.</summary>
    private sealed class UiProgress(ScanViewModel owner) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => owner._postToUi(() => owner.ApplyProgress(value));
    }
}
