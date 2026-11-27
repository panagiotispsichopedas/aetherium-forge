using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Pages.Models;

/// <summary>
/// The Models page (IMPLEMENTATION_GUIDE §9 "Model download" and "GPU auto profile", §14 Models and Settings): installed and downloadable
/// models with downloads, verification, deletion and custom models, plus the GPU adapters and the per-family calibration.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    /// <summary>
    /// The share of dedicated video memory below which the available budget triggers the "close GPU-heavy apps" warning. A running game
    /// once made calibration pick a far too small tile.
    /// </summary>
    public const double VramWarningFraction = 0.5;

    /// <summary>The largest scale a custom model may declare.</summary>
    public const int MaxCustomScale = 16;

    /// <summary>The longest display name a custom model may have.</summary>
    public const int MaxCustomDisplayNameLength = 80;

    private static readonly string[] _calibrationSteps = [GpuProfiler.PrecisionStep, GpuProfiler.TileStep, GpuProfiler.ThroughputStep];

    private readonly Func<ModelLibrary> _libraryFactory;
    private readonly ModelDownloadCoordinator _downloads;
    private readonly IGpuAdapterProvider _adapterProvider;
    private readonly GpuProfiler _profiler;
    private readonly GpuProfileStore _profileStore;
    private readonly IModelDialogs _dialogs;
    private readonly IModelsPageSettings _settings;
    private readonly INavigationService? _navigation;
    private readonly IForgeRunner? _runner;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly HashSet<string> _offerCalibrationFor = new(StringComparer.OrdinalIgnoreCase);
    private ModelLibrary? _library;
    private GpuAdapterInfo? _selectedAdapter;
    private GpuTier? _tier;
    private CancellationTokenSource? _calibrationCts;
    private int _loadVersion;
    private Task _latestLoad = Task.CompletedTask;
    private bool _loaded;
    private bool _firstRunChecked;

    /// <summary>Gets or sets a value indicating whether the model list is loading.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Gets or sets where the model list came from, or why it is missing.</summary>
    [ObservableProperty]
    private string? _manifestStatus;

    /// <summary>Gets or sets a value indicating whether <see cref="ManifestStatus"/> is a warning (offline, rate-limited, unavailable).</summary>
    [ObservableProperty]
    private bool _isManifestWarning;

    /// <summary>Gets or sets the result of the last page action.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Gets or sets a value indicating whether a pipeline run is active (calibration is disabled meanwhile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate))]
    private bool _isRunActive;

    /// <summary>Gets or sets a value indicating whether a calibration is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate))]
    [NotifyCanExecuteChangedFor(nameof(CancelCalibrationCommand))]
    private bool _isCalibrating;

    /// <summary>Gets or sets the calibration status line.</summary>
    [ObservableProperty]
    private string? _calibrationStatus;

    /// <summary>Gets or sets the calibration progress, 0 to 100.</summary>
    [ObservableProperty]
    private double _calibrationPercent;

    /// <summary>Gets or sets the video-memory warning, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVramWarning))]
    private string? _vramWarning;

    /// <summary>Gets or sets the selected adapter as text.</summary>
    [ObservableProperty]
    private string _selectedAdapterText = "—";

    /// <summary>Gets or sets the selected adapter's tier and recommendation as text.</summary>
    [ObservableProperty]
    private string _tierText = "—";

    /// <summary>Gets or sets a value indicating whether the custom-model form is open.</summary>
    [ObservableProperty]
    private bool _isCustomFormOpen;

    /// <summary>Gets or sets the picked custom <c>.onnx</c> file.</summary>
    [ObservableProperty]
    private string _customSourcePath = string.Empty;

    /// <summary>Gets or sets the custom model's display name.</summary>
    [ObservableProperty]
    private string _customDisplayName = string.Empty;

    /// <summary>Gets or sets the custom model's scale.</summary>
    [ObservableProperty]
    private decimal? _customScale = 4;

    /// <summary>Gets or sets the custom model's precision, <c>fp32</c> or <c>fp16</c>.</summary>
    [ObservableProperty]
    private string _customPrecision = GpuProfiler.Fp32;

    /// <summary>Gets or sets the custom-model validation error, or <see langword="null"/>.</summary>
    [ObservableProperty]
    private string? _customError;

    /// <summary>Gets or sets a value indicating whether a custom model is being copied (the form's commands are disabled meanwhile).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCustomModelCommand), nameof(SaveCustomModelCommand))]
    private bool _isSavingCustom;

    /// <summary>
    /// Gets or sets a value indicating whether the page offers the recommended models again: the list is loaded, nothing is installed and
    /// nothing downloads. This is how the first-run prompt comes back after "Later".
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OfferRecommendedModelsCommand))]
    private bool _canOfferRecommended;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelsViewModel"/> class.
    /// </summary>
    /// <param name="libraryFactory">Creates a model library (one per listing, so the manifest origin is current).</param>
    /// <param name="downloads">The app-wide download coordinator.</param>
    /// <param name="adapterProvider">Enumerates GPU adapters.</param>
    /// <param name="profiler">Selects, classifies and calibrates adapters.</param>
    /// <param name="profileStore">Stores calibration profiles.</param>
    /// <param name="dialogs">Shows confirmations and prompts.</param>
    /// <param name="settings">Stores page preferences (the dismissed first-run prompt).</param>
    /// <param name="navigation">Page navigation, or <see langword="null"/>.</param>
    /// <param name="runner">The pipeline runner, or <see langword="null"/> when none is registered.</param>
    /// <param name="post">Runs an action on the UI thread; defaults to running it inline.</param>
    /// <param name="logger">Receives page events.</param>
    public ModelsViewModel(
        Func<ModelLibrary> libraryFactory,
        ModelDownloadCoordinator downloads,
        IGpuAdapterProvider adapterProvider,
        GpuProfiler profiler,
        GpuProfileStore profileStore,
        IModelDialogs dialogs,
        IModelsPageSettings settings,
        INavigationService? navigation = null,
        IForgeRunner? runner = null,
        Action<Action>? post = null,
        ILogger<ModelsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(libraryFactory);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(adapterProvider);
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);

        _libraryFactory = libraryFactory;
        _downloads = downloads;
        _adapterProvider = adapterProvider;
        _profiler = profiler;
        _profileStore = profileStore;
        _dialogs = dialogs;
        _settings = settings;
        _navigation = navigation;
        _runner = runner;
        _post = post ?? (action => action());
        _logger = logger ?? (ILogger)NullLogger.Instance;

        downloads.OperationChanged += (_, operation) => _post(() => OnDownloadChanged(operation));
        if (runner is not null)
        {
            _isRunActive = runner.State != PipelineState.Idle;
            runner.RunStateChanged += (_, _) => _post(UpdateRunState);
        }

        if (navigation is not null)
        {
            navigation.Navigated += (_, page) =>
            {
                if (page == AppPage.Models)
                {
                    Track(OnVisitedAsync);
                }
            };
        }
    }

    /// <summary>Gets the model rows.</summary>
    public ObservableCollection<ModelRowViewModel> Models { get; } = [];

    /// <summary>Gets the GPU adapters.</summary>
    public ObservableCollection<GpuAdapterRow> Adapters { get; } = [];

    /// <summary>Gets the installed model families and their calibration.</summary>
    public ObservableCollection<GpuFamilyRow> Families { get; } = [];

    /// <summary>Gets the precisions a custom model can declare.</summary>
    public IReadOnlyList<string> AvailablePrecisions { get; } = [GpuProfiler.Fp32, GpuProfiler.Fp16];

    /// <summary>Gets a value indicating whether <see cref="VramWarning"/> is set.</summary>
    public bool HasVramWarning => VramWarning is not null;

    /// <summary>Gets a value indicating whether a calibration may start: an adapter is selected, and no calibration or run is active.</summary>
    public bool CanCalibrate => _selectedAdapter is not null && !IsCalibrating && !IsRunActive;

    /// <summary>Gets the selected adapter's tier, or <see langword="null"/> before the GPU section loaded or without an adapter.</summary>
    public GpuTier? Tier => _tier;

    /// <summary>Gets background work started by events (reloads, prompts); tests await it.</summary>
    internal Task BackgroundWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Loads the model list and the GPU section.
    /// </summary>
    /// <returns>A task that completes when loaded.</returns>
    [RelayCommand]
    public Task RefreshAsync()
    {
        var load = LoadAsync(++_loadVersion);
        _latestLoad = load;
        return load;
    }

    private async Task LoadAsync(int version)
    {
        IsLoading = true;
        try
        {
            var library = _libraryFactory();
            var entries = await library.ListAsync(CancellationToken.None);
            if (version != _loadVersion)
            {
                // A newer refresh started meanwhile; its result wins, so wait for it instead of applying this stale listing.
                await _latestLoad;
                return;
            }

            _library = library;
            ApplyManifestStatus(library.LastManifest);
            Models.Clear();
            foreach (var entry in entries)
            {
                Models.Add(new ModelRowViewModel(this, entry) { Download = _downloads.Find(entry.Name) });
            }

            await RefreshGpuAsync();
            _loaded = true;
            UpdateCanOfferRecommended();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogLoadFailed(_logger, exception);
            StatusMessage = $"The models folder could not be read: {exception.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Shows the first-run prompt once per session when no model is installed: it recommends the model family for this GPU's tier.
    /// After the user chose "Later" the prompt no longer appears on its own; the page's <see cref="OfferRecommendedModelsCommand"/>
    /// offers it again.
    /// </summary>
    /// <returns>A task that completes when the prompt was answered (or not needed).</returns>
    public async Task RunFirstRunCheckAsync()
    {
        if (_firstRunChecked)
        {
            return;
        }

        _firstRunChecked = true;
        if (await _settings.IsFirstRunPromptDismissedAsync(CancellationToken.None))
        {
            return;
        }

        if (!_loaded)
        {
            await RefreshAsync();
        }

        await OfferRecommendedAsync(fromPage: false);
    }

    /// <summary>Shows the recommendation again from the Models page (also after "Later").</summary>
    /// <returns>A task that completes when the prompt was answered.</returns>
    [RelayCommand(CanExecute = nameof(CanOfferRecommended))]
    private Task OfferRecommendedModelsAsync() => OfferRecommendedAsync(fromPage: true);

    private async Task OfferRecommendedAsync(bool fromPage)
    {
        if (Models.Any(row => row.IsInstalled) || _downloads.HasActiveDownloads)
        {
            return;
        }

        var tier = _tier ?? GpuTier.Low;
        var family = _profiler.RecommendModelFamily(tier);
        var models = RecommendedDownloads(family);
        if (models.Count == 0)
        {
            StatusMessage = "No model is installed and the model list is unavailable. Connect to the internet and press Refresh.";
            return;
        }

        var recommendation = new ModelFirstRunRecommendation(_selectedAdapter?.Name, tier, family, [.. models.Select(ToItem)]);
        var choice = await _dialogs.ShowFirstRunAsync(recommendation);
        switch (choice)
        {
            case ModelFirstRunChoice.DownloadRecommended:
                foreach (var model in models)
                {
                    StartDownload(model);
                }

                StatusMessage = $"Downloading {family}.";
                break;
            case ModelFirstRunChoice.Choose:
                if (!fromPage)
                {
                    _navigation?.NavigateTo(AppPage.Models);
                }

                break;
            default:
                // Remember "Later" so the prompt stops appearing at startup; "Recommended models…" on this page brings it back.
                await _settings.SetFirstRunPromptDismissedAsync(true, CancellationToken.None);
                StatusMessage = "No model is installed yet. Download one before running the pipeline, or use Recommended models… on this page.";
                break;
        }
    }

    /// <summary>Calibrates a model family on the selected adapter and saves the profile.</summary>
    /// <param name="row">The family.</param>
    /// <returns>A task that completes when the calibration ended.</returns>
    internal async Task CalibrateAsync(GpuFamilyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // TODO(GpuWorkLock): the IForgeRunner check only sees runs of this process, and the runner contract cannot stop a run from starting
        // while a calibration runs. When the cross-process GpuWorkLock (Core/Concurrency) merges, acquire it here for the whole calibration
        // (fail fast with a "GPU busy" status when another process holds it) and release it in the finally block below.
        if (!CanCalibrate || IsRunnerBusy || _selectedAdapter is not { } adapter)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _calibrationCts = cancellation;
        IsCalibrating = true;
        UpdateCanCalibrate();
        CalibrationPercent = 0;
        CalibrationStatus = $"Calibrating {row.Family} on {adapter.Name}…";
        var progress = new CalibrationProgressSink(report => _post(() => ApplyCalibrationProgress(row.Family, report)));
        try
        {
            await UpdateVramWarningAsync();
            if (IsRunnerBusy)
            {
                // A run started while the video memory was queried.
                CalibrationStatus = "A pipeline run started; calibrate after it ends.";
                return;
            }

            var profile = await _profiler.CalibrateAsync(adapter, row.Variants, null, progress, cancellation.Token);
            var saved = await _profileStore.SaveAsync(profile, CancellationToken.None);
            var summary = $"{row.Family}: {ModelFormatting.Precision(profile.Precision)}, tile {profile.TileSize}, {ModelFormatting.Number(profile.SecondsPerMegapixel, "0.00")} s/MP";
            CalibrationStatus = saved
                ? $"Calibrated {summary}."
                : $"Measured {summary}, but the profile was not saved (provisional: the FP16 variant failed to load). Calibration runs again next time.";
            CalibrationPercent = 100;
            await RefreshProfilesAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CalibrationStatus = "Calibration cancelled.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // VideoMemoryExhaustedException, OnnxRuntimeException (device removed) or invalid variants: show it and keep the page usable.
            LogCalibrationFailed(_logger, exception, row.Family);
            CalibrationStatus = $"Calibration of {row.Family} failed: {exception.Message}";
        }
        finally
        {
            _calibrationCts = null;
            IsCalibrating = false;
            UpdateCanCalibrate();
        }
    }

    /// <summary>Asks for confirmation (size and licence), then starts or resumes a download.</summary>
    /// <param name="row">The model.</param>
    /// <returns>A task that completes when the download started or was declined.</returns>
    internal async Task DownloadAsync(ModelRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanDownload || row.Entry.Available is not { } model)
        {
            return;
        }

        if (!await _dialogs.ConfirmDownloadAsync([ToItem(model)]))
        {
            return;
        }

        if (row.IsVerifying)
        {
            // An update replaces the file the verification is hashing.
            row.Message = "Wait for the verification to finish, then download.";
            return;
        }

        row.Message = null;
        StartDownload(model);
    }

    /// <summary>Re-hashes an installed model and compares it with the manifest.</summary>
    /// <param name="row">The model.</param>
    /// <returns>A task that completes when verified.</returns>
    internal async Task VerifyAsync(ModelRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsInstalled || row.IsVerifying)
        {
            return;
        }

        if (IsTransferActive(row))
        {
            // The transfer moves a new file over the one being hashed: the result would describe neither file.
            row.Message = "Verify after the download ends.";
            return;
        }

        row.IsVerifying = true;
        row.Message = "Verifying…";
        try
        {
            var library = _library ?? _libraryFactory();
            var sha = await library.ComputeInstalledSha256Async(row.Name, CancellationToken.None);
            var expected = row.Entry.Available?.Sha256;
            row.Message = expected is null
                ? $"SHA-256 {sha[..16]}… (no published hash to compare with)."
                : string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase)
                    ? "Verified: the SHA-256 matches the published file."
                    : row.State == ModelState.UpdateAvailable
                        ? "The file differs from the newest release: an update is available."
                        : "The SHA-256 does not match the published file. Delete the model and download it again.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogVerifyFailed(_logger, exception, row.Name);
            row.Message = $"Verification failed: {exception.Message}";
        }
        finally
        {
            row.IsVerifying = false;
        }
    }

    /// <summary>Deletes an installed model after confirmation.</summary>
    /// <param name="row">The model.</param>
    /// <returns>A task that completes when deleted or declined.</returns>
    internal async Task DeleteAsync(ModelRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsInstalled)
        {
            return;
        }

        if (DeleteBlockedReason(row) is { } blocked)
        {
            row.Message = blocked;
            return;
        }

        if (!await _dialogs.ConfirmDeleteAsync(row.DisplayName, PartialSize(row)))
        {
            return;
        }

        // The dialog may have stayed open while a download, run, calibration or verification started.
        if (DeleteBlockedReason(row) is { } blockedNow)
        {
            row.Message = blockedNow;
            return;
        }

        await DeleteFilesAsync(row, $"Deleted {row.DisplayName}.");
    }

    /// <summary>Deletes a leftover partial download after a confirmation that shows its size.</summary>
    /// <param name="row">The model.</param>
    /// <returns>A task that completes when deleted or declined.</returns>
    internal async Task DiscardPartialAsync(ModelRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanDiscardPartial || PartialSize(row) is not { } size)
        {
            return;
        }

        if (!await _dialogs.ConfirmDiscardPartialAsync(row.DisplayName, size))
        {
            return;
        }

        if (IsTransferActive(row))
        {
            row.Message = "The download was resumed meanwhile; cancel it before discarding the partial file.";
            return;
        }

        await DeleteFilesAsync(row, $"Discarded the partial download of {row.DisplayName}.");
    }

    /// <summary>Opens the custom-model form for a picked file.</summary>
    /// <param name="sourcePath">The <c>.onnx</c> file.</param>
    internal void OpenCustomForm(string sourcePath)
    {
        CustomSourcePath = sourcePath;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        CustomDisplayName = name;
        CustomScale = 4;
        CustomPrecision = name.Contains(GpuProfiler.Fp16, StringComparison.OrdinalIgnoreCase) ? GpuProfiler.Fp16 : GpuProfiler.Fp32;
        CustomError = null;
        IsCustomFormOpen = true;
    }

    /// <summary>Validates the custom-model form.</summary>
    /// <returns>The first problem, or <see langword="null"/> when the form is valid.</returns>
    internal string? ValidateCustomModel()
    {
        if (string.IsNullOrWhiteSpace(CustomSourcePath) || !File.Exists(CustomSourcePath))
        {
            return "Pick an existing .onnx file.";
        }

        if (!string.Equals(Path.GetExtension(CustomSourcePath), ".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return "The file must have the .onnx extension.";
        }

        var name = Path.GetFileNameWithoutExtension(CustomSourcePath);
        if (!SafeModelName().IsMatch(name))
        {
            return "Rename the file: use letters, digits, '.', '-' or '_' only, starting with a letter or digit.";
        }

        var modelsFolder = Path.GetFullPath(ModelsFolder);
        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(CustomSourcePath));
        if (string.Equals(sourceFolder?.TrimEnd(Path.DirectorySeparatorChar), modelsFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return "This file is already in the models folder.";
        }

        if (Models.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(modelsFolder, name + ".onnx")))
        {
            return $"A model named '{name}' already exists or is reserved for a downloadable model. Rename the file.";
        }

        if (string.IsNullOrWhiteSpace(CustomDisplayName) || CustomDisplayName.Trim().Length > MaxCustomDisplayNameLength)
        {
            return $"Enter a display name of 1 to {MaxCustomDisplayNameLength} characters.";
        }

        if (CustomScale is not { } scale || scale != decimal.Truncate(scale) || scale < 1 || scale > MaxCustomScale)
        {
            return $"The scale must be a whole number from 1 to {MaxCustomScale}.";
        }

        return AvailablePrecisions.Contains(CustomPrecision, StringComparer.Ordinal) ? null : "The precision must be FP32 or FP16.";
    }

    /// <summary>Gets the models folder.</summary>
    internal string ModelsFolder => (_library ??= _libraryFactory()).ModelsFolder;

    /// <summary>Lets the user pick a custom <c>.onnx</c> file and opens the form.</summary>
    /// <returns>A task that completes when the file was picked or the picker was cancelled.</returns>
    [RelayCommand(CanExecute = nameof(CanEditCustomModel))]
    private async Task AddCustomModelAsync()
    {
        if (IsSavingCustom)
        {
            return;
        }

        var path = await _dialogs.PickModelFileAsync();
        if (path is not null && !IsSavingCustom)
        {
            OpenCustomForm(path);
        }
    }

    /// <summary>
    /// Copies the custom model into the models folder and writes its sidecar. The <c>.onnx</c> is moved into place first, without
    /// overwriting; the sidecar is then written to a temporary file and moved next to it. If the sidecar fails, the copied model is removed
    /// again, so a model never appears with a missing or half-written sidecar.
    /// </summary>
    /// <returns>A task that completes when the model was added or the form shows an error.</returns>
    [RelayCommand(CanExecute = nameof(CanEditCustomModel))]
    private async Task SaveCustomModelAsync()
    {
        if (IsSavingCustom)
        {
            return;
        }

        CustomError = ValidateCustomModel();
        if (CustomError is not null)
        {
            return;
        }

        // Read the form once: it must not change what is written while the copy runs.
        var source = CustomSourcePath;
        var displayName = CustomDisplayName.Trim();
        var scale = (int)CustomScale!.Value;
        var precision = CustomPrecision;
        var name = Path.GetFileNameWithoutExtension(source);
        var folder = ModelsFolder;
        var target = Path.Combine(folder, name + ".onnx");
        var temporary = target + ".copying";
        var sidecar = Path.ChangeExtension(target, ".json");
        var sidecarTemporary = sidecar + ".writing";
        var modelMoved = false;
        IsSavingCustom = true;
        try
        {
            Directory.CreateDirectory(folder);
            await CopyFileAsync(source, temporary);
            File.Move(temporary, target, overwrite: false);
            modelMoved = true;
            await WriteSidecarAsync(sidecarTemporary, displayName, scale, precision);
            File.Move(sidecarTemporary, sidecar, overwrite: true);
            LogCustomAdded(_logger, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCustomFailed(_logger, exception, name);
            CustomError = $"The model could not be added: {exception.Message}";
            if (modelMoved)
            {
                // Roll back: without its sidecar the model would load with a guessed scale and precision.
                DeleteFileLogged(target);
            }

            return;
        }
        finally
        {
            DeleteFileLogged(temporary);
            DeleteFileLogged(sidecarTemporary);
            IsSavingCustom = false;
        }

        IsCustomFormOpen = false;
        StatusMessage = $"Added custom model {displayName}.";
        await RefreshAsync();
    }

    private bool CanEditCustomModel() => !IsSavingCustom;

    /// <summary>Closes the custom-model form.</summary>
    [RelayCommand]
    private void CancelCustomModel()
    {
        IsCustomFormOpen = false;
        CustomError = null;
    }

    /// <summary>Cancels the running calibration.</summary>
    [RelayCommand(CanExecute = nameof(IsCalibrating))]
    private void CancelCalibration() => _calibrationCts?.Cancel();

    private static ModelDownloadItem ToItem(ManifestModel model) => new(model.DisplayName ?? model.Name, model.Size, model.Licence, model.LicenceUrl);

    private static async Task CopyFileAsync(string source, string destination)
    {
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input)
        {
            var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            await using (output)
            {
                await input.CopyToAsync(output);
            }
        }
    }

    private static async Task WriteSidecarAsync(string path, string displayName, int scale, string precision)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (stream)
        {
            var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            await using (writer)
            {
                writer.WriteStartObject();
                writer.WriteString("displayName", displayName);
                writer.WriteNumber("scale", scale);
                writer.WriteString("precision", precision);
                writer.WriteStartArray("recommendedFor");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }
    }

    private void DeleteFileLogged(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup only: the add already reported its result. Log the leftover so it can be found.
            LogCleanupFailed(_logger, exception, path);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeModelName();

    private async Task OnVisitedAsync()
    {
        if (!_loaded)
        {
            await RefreshAsync();
            await RunFirstRunCheckAsync();
            return;
        }

        // Other apps may have started or stopped using the GPU since the last visit.
        await RefreshGpuAsync();
    }

    private void StartDownload(ManifestModel model)
    {
        _offerCalibrationFor.Add(GpuProfiler.GetModelFamily(model.Name));
        var operation = _downloads.Start(model);
        if (FindRow(model.Name) is { } row)
        {
            row.Download = operation;
        }
    }

    private void OnDownloadChanged(ModelDownloadOperation operation)
    {
        if (FindRow(operation.Name) is { } row)
        {
            if (ReferenceEquals(row.Download, operation))
            {
                row.RefreshDownload();
            }
            else
            {
                row.Download = operation;
            }

            row.Message = operation.Status switch
            {
                ModelDownloadStatus.Failed => $"Download failed: {Describe(operation.Error)}",
                ModelDownloadStatus.Cancelled => "Download cancelled. The partial file is kept; Resume download continues it.",
                _ => row.Message,
            };
        }

        UpdateCanOfferRecommended();
        switch (operation.Status)
        {
            case ModelDownloadStatus.Completed:
                Track(() => AfterDownloadAsync(operation));
                break;
            case ModelDownloadStatus.Failed or ModelDownloadStatus.Cancelled:
                Track(RefreshAsync);
                break;
        }
    }

    private async Task AfterDownloadAsync(ModelDownloadOperation operation)
    {
        StatusMessage = $"Installed {operation.Model.DisplayName ?? operation.Name}.";
        await RefreshAsync();
        var family = GpuProfiler.GetModelFamily(operation.Name);
        if (_downloads.Operations.Any(other => other.IsActive && string.Equals(GpuProfiler.GetModelFamily(other.Name), family, StringComparison.OrdinalIgnoreCase))
            || !_offerCalibrationFor.Remove(family))
        {
            return;
        }

        var row = Families.FirstOrDefault(candidate => string.Equals(candidate.Family, family, StringComparison.OrdinalIgnoreCase));
        if (row is null || !CanCalibrate)
        {
            return;
        }

        if (await _dialogs.OfferCalibrationAsync(family))
        {
            await CalibrateAsync(row);
        }
    }

    private async Task DeleteFilesAsync(ModelRowViewModel row, string doneMessage)
    {
        try
        {
            var library = _library ?? _libraryFactory();
            await library.DeleteAsync(row.Name);
            StatusMessage = doneMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(_logger, exception, row.Name);
            row.Message = $"The model could not be deleted: {exception.Message}";
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshGpuAsync()
    {
        // DXGI enumeration can take a while (driver calls, a waking dGPU): never on the UI thread.
        var adapters = await Task.Run(_adapterProvider.GetAdapters);
        _selectedAdapter = _profiler.SelectAdapter(adapters);
        Adapters.Clear();
        foreach (var adapter in adapters)
        {
            Adapters.Add(new GpuAdapterRow(adapter, _selectedAdapter is not null && adapter.Luid == _selectedAdapter.Luid));
        }

        if (_selectedAdapter is { } selected)
        {
            _tier = _profiler.ClassifyTier(selected);
            SelectedAdapterText = $"{selected.Name} ({ModelFormatting.Bytes(selected.DedicatedVideoMemory)})";
            TierText = $"{_tier} tier: recommends {_profiler.RecommendModelFamily(_tier.Value)}";
        }
        else
        {
            _tier = null;
            SelectedAdapterText = "No usable GPU adapter found";
            TierText = "—";
        }

        OnPropertyChanged(nameof(Tier));
        await UpdateVramWarningAsync();

        Families.Clear();
        foreach (var group in Models.Where(model => model.Entry.Installed is not null).GroupBy(model => model.Family, StringComparer.OrdinalIgnoreCase))
        {
            Families.Add(new GpuFamilyRow(this, group.Key, [.. group.Select(model => model.Entry.Installed!)]));
        }

        await RefreshProfilesAsync();
    }

    private async Task RefreshProfilesAsync()
    {
        IReadOnlyList<GpuProfile> profiles = _selectedAdapter is null ? [] : await _profileStore.ListAsync(CancellationToken.None);
        foreach (var row in Families)
        {
            List<GpuProfile> matching = _selectedAdapter is { } adapter
                ? profiles.Where(profile => profile.MatchesHardware(adapter) && string.Equals(profile.Model, row.Family, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            row.Profile = matching.FirstOrDefault(profile => !GpuProfileStore.NeedsRecalibration(profile, _selectedAdapter!));
            row.NeedsRecalibration = row.Profile is null && matching.Count > 0;
        }

        UpdateCanCalibrate();
    }

    private async Task UpdateVramWarningAsync()
    {
        if (_selectedAdapter is not { IsIntegrated: false, DedicatedVideoMemory: > 0 } adapter)
        {
            VramWarning = null;
            return;
        }

        // QueryVideoMemoryInfo is a DXGI call: never on the UI thread.
        var memory = await Task.Run(() => _adapterProvider.QueryVideoMemory(adapter.Luid));
        if (memory is null || memory.Available >= adapter.DedicatedVideoMemory * VramWarningFraction)
        {
            VramWarning = null;
            return;
        }

        VramWarning = $"Only {ModelFormatting.Bytes(memory.Available)} of {ModelFormatting.Bytes(adapter.DedicatedVideoMemory)} video memory is available: other "
            + "processes are using the GPU. Close games or GPU-heavy apps first, or calibration picks a tile that is too small.";
    }

    /// <summary>Gets a value indicating whether the runner is not idle right now (read directly, not from the posted <see cref="IsRunActive"/>).</summary>
    private bool IsRunnerBusy => _runner is not null && _runner.State != PipelineState.Idle;

    private bool IsTransferActive(ModelRowViewModel row) => row.IsTransferActive || _downloads.Find(row.Name)?.IsActive == true;

    private string? DeleteBlockedReason(ModelRowViewModel row) =>
        IsTransferActive(row) ? "Cancel the download before deleting the model."
        : IsRunActive || IsRunnerBusy || IsCalibrating ? "A run or calibration is using the models; delete it after it ends."
        : row.IsVerifying ? "Wait for the verification to finish, then delete the model."
        : null;

    private long? PartialSize(ModelRowViewModel row)
    {
        if (!row.HasPartialDownload)
        {
            return null;
        }

        var fileName = row.Entry.Available?.FileName ?? row.Name + ".onnx";
        var partial = new FileInfo(Path.Combine(ModelsFolder, fileName + ModelDownloader.PartialSuffix));
        return partial.Exists ? partial.Length : null;
    }

    private void UpdateCanOfferRecommended() =>
        CanOfferRecommended = _loaded && !Models.Any(row => row.IsInstalled) && !_downloads.HasActiveDownloads;

    private void UpdateRunState()
    {
        IsRunActive = _runner is not null && _runner.State != PipelineState.Idle;
        UpdateCanCalibrate();
    }

    private void UpdateCanCalibrate()
    {
        OnPropertyChanged(nameof(CanCalibrate));
        foreach (var row in Families)
        {
            row.CanCalibrate = CanCalibrate;
        }
    }

    private void ApplyCalibrationProgress(string family, CalibrationProgress report)
    {
        var step = Array.IndexOf(_calibrationSteps, report.Step);
        if (step < 0)
        {
            return;
        }

        var within = report.Total > 0 ? Math.Clamp((double)report.Done / report.Total, 0, 1) : 0;
        CalibrationPercent = (step + within) / _calibrationSteps.Length * 100;
        var label = report.Step switch
        {
            GpuProfiler.PrecisionStep => "comparing FP16 with FP32",
            GpuProfiler.TileStep => "finding the largest tile that fits",
            _ => "measuring throughput",
        };
        CalibrationStatus = $"Calibrating {family}: {label} ({report.Done}/{report.Total})…";
    }

    private void ApplyManifestStatus(ModelManifest? manifest)
    {
        (ManifestStatus, IsManifestWarning) = manifest switch
        {
            null => ("The model list could not be loaded (offline, rate-limited by GitHub, or an unsupported list). Only installed models are shown.", true),
            { Origin: ManifestOrigin.Cache } => ($"Offline or rate-limited by GitHub: showing the cached model list ({manifest.Release}).", true),
            _ => ($"Model list: {manifest.Release}", false),
        };
    }

    private List<ManifestModel> RecommendedDownloads(string family) =>
    [
        .. Models
            .Where(row => !row.IsInstalled && row.Entry.Available is not null && string.Equals(row.Family, family, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Entry.Available!),
    ];

    private ModelRowViewModel? FindRow(string name) => Models.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Describe(Exception? error) => error switch
    {
        null => "unknown error.",
        ModelHashMismatchException => "the file did not match its published SHA-256 and was deleted. Try again.",
        ModelSizeMismatchException => "the server sent more data than expected; the file was deleted. Try again.",
        UntrustedModelSourceException => "the download was redirected to an untrusted host; nothing was saved.",
        HttpRequestException http => $"network error ({http.Message}). The partial file is kept; Resume download continues it.",
        IOException io => $"{io.Message} The partial file is kept; Resume download continues it.",
        _ => error.Message,
    };

    private void Track(Func<Task> work) => BackgroundWork = Task.WhenAll(BackgroundWork, RunLoggedAsync(work));

    private async Task RunLoggedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Event-driven work has no caller to report to: log it and show it on the page.
            LogBackgroundFailed(_logger, exception);
            StatusMessage = exception.Message;
        }
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Error, Message = "Loading the model list failed")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 411, Level = LogLevel.Error, Message = "Calibration of {Family} failed")]
    private static partial void LogCalibrationFailed(ILogger logger, Exception exception, string family);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Verification of model {Model} failed")]
    private static partial void LogVerifyFailed(ILogger logger, Exception exception, string model);

    [LoggerMessage(EventId = 413, Level = LogLevel.Error, Message = "Deleting model {Model} failed")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string model);

    [LoggerMessage(EventId = 414, Level = LogLevel.Information, Message = "Added custom model {Model}")]
    private static partial void LogCustomAdded(ILogger logger, string model);

    [LoggerMessage(EventId = 415, Level = LogLevel.Error, Message = "Adding custom model {Model} failed")]
    private static partial void LogCustomFailed(ILogger logger, Exception exception, string model);

    [LoggerMessage(EventId = 416, Level = LogLevel.Error, Message = "Models page background work failed")]
    private static partial void LogBackgroundFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 417, Level = LogLevel.Warning, Message = "Removing the leftover file {Path} failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, string path);

    /// <summary>A synchronous progress sink (System.Progress would capture a context the calibration thread does not need).</summary>
    private sealed class CalibrationProgressSink(Action<CalibrationProgress> report) : IProgress<CalibrationProgress>
    {
        public void Report(CalibrationProgress value) => report(value);
    }
}
