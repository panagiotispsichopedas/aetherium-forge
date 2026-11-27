using AetheriumForge.App.Services;
using AetheriumForge.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetheriumForge.App.Pages.Models;

/// <summary>
/// One row of the model list: a manifest model, an installed model, or both, plus its running download.
/// </summary>
public sealed partial class ModelRowViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;

    /// <summary>Gets or sets the library entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(DisplayName), nameof(SizeText), nameof(Precision), nameof(Tier), nameof(Licence), nameof(LicenceUrl), nameof(HasLicenceUrl), nameof(IsInstalled), nameof(CanDownload), nameof(DownloadLabel), nameof(StateText), nameof(HasPartialDownload), nameof(CanDiscardPartial), nameof(CanVerify))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private ModelEntry _entry;

    /// <summary>Gets or sets the latest download of this model, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading), nameof(IsPaused), nameof(IsTransferActive), nameof(CanDownload), nameof(StateText), nameof(ProgressPercent), nameof(ProgressText), nameof(CanDiscardPartial), nameof(CanVerify))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private ModelDownloadOperation? _download;

    /// <summary>Gets or sets the result of the last action on this row (verification, failure), or <see langword="null"/>.</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>Gets or sets a value indicating whether a verification is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerify))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private bool _isVerifying;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelRowViewModel"/> class.
    /// </summary>
    /// <param name="owner">The page.</param>
    /// <param name="entry">The library entry.</param>
    internal ModelRowViewModel(ModelsViewModel owner, ModelEntry entry)
    {
        _owner = owner;
        _entry = entry;
    }

    /// <summary>Gets the model name.</summary>
    public string Name => Entry.Name;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => Entry.DisplayName;

    /// <summary>Gets the state from the library.</summary>
    public ModelState State => Entry.State;

    /// <summary>Gets the model family (the name without a precision suffix).</summary>
    public string Family => Core.Gpu.GpuProfiler.GetModelFamily(Entry.Name);

    /// <summary>Gets the download size, or the installed file size for a custom model.</summary>
    public long? Size => Entry.Available?.Size ?? InstalledSize();

    /// <summary>Gets the size as text.</summary>
    public string SizeText => Size is { } size ? ModelFormatting.Bytes(size) : "—";

    /// <summary>Gets the precision as text.</summary>
    public string Precision => ModelFormatting.Precision(Entry.Installed?.Precision ?? Entry.Available?.Precision);

    /// <summary>Gets the recommended GPU tier as text.</summary>
    public string Tier => ModelFormatting.Tier(Entry.Available?.Tier);

    /// <summary>Gets the licence identifier as text.</summary>
    public string Licence => Entry.Available?.Licence ?? Entry.Installed?.Licence ?? "Unknown";

    /// <summary>Gets the licence URL, or <see langword="null"/>.</summary>
    public Uri? LicenceUrl => Entry.Available?.LicenceUrl;

    /// <summary>Gets a value indicating whether <see cref="LicenceUrl"/> is set.</summary>
    public bool HasLicenceUrl => LicenceUrl is not null;

    /// <summary>Gets a value indicating whether the model is installed.</summary>
    public bool IsInstalled => Entry.Installed is not null;

    /// <summary>Gets a value indicating whether an interrupted download can be resumed.</summary>
    public bool HasPartialDownload => Entry.HasPartialDownload;

    /// <summary>Gets a value indicating whether the transfer is running (not paused).</summary>
    public bool IsDownloading => Download?.Status == ModelDownloadStatus.Downloading;

    /// <summary>Gets a value indicating whether the transfer is paused.</summary>
    public bool IsPaused => Download?.Status == ModelDownloadStatus.Paused;

    /// <summary>Gets a value indicating whether the transfer is downloading or paused.</summary>
    public bool IsTransferActive => Download?.IsActive == true;

    /// <summary>Gets a value indicating whether Download (or Update / Resume) is offered.</summary>
    public bool CanDownload => Entry.Available is not null && Entry.State is ModelState.NotInstalled or ModelState.UpdateAvailable && !IsTransferActive;

    /// <summary>
    /// Gets a value indicating whether Verify is offered: the model is installed, no verification runs, and no download or update of it is
    /// active (the transfer replaces the file being hashed).
    /// </summary>
    public bool CanVerify => IsInstalled && !IsVerifying && !IsTransferActive;

    /// <summary>Gets a value indicating whether a leftover partial file can be discarded.</summary>
    public bool CanDiscardPartial => HasPartialDownload && Entry.Installed is null && !IsTransferActive;

    /// <summary>Gets the label of the download button.</summary>
    public string DownloadLabel => Entry.State == ModelState.UpdateAvailable ? "Update" : HasPartialDownload ? "Resume download" : "Download";

    /// <summary>Gets the download progress, 0 to 100.</summary>
    public double ProgressPercent => (Download?.Progress?.Fraction ?? 0) * 100;

    /// <summary>Gets the progress as text: bytes done of total and the transfer rate.</summary>
    public string ProgressText
    {
        get
        {
            if (Download?.Progress is not { } progress)
            {
                return Download?.IsActive == true ? "Starting…" : string.Empty;
            }

            var text = $"{ModelFormatting.Bytes(progress.BytesDone)} of {ModelFormatting.Bytes(progress.BytesTotal)}";
            return Download.Status == ModelDownloadStatus.Downloading && progress.BytesPerSecond > 0
                ? $"{text} · {ModelFormatting.Bytes(progress.BytesPerSecond)}/s"
                : text;
        }
    }

    /// <summary>Gets the state as text, including a running download.</summary>
    public string StateText => Download?.Status switch
    {
        ModelDownloadStatus.Downloading => $"Downloading {ModelFormatting.Number(ProgressPercent, "0")}%",
        ModelDownloadStatus.Paused => "Paused",
        _ => Entry.State switch
        {
            ModelState.NotInstalled when Entry.HasPartialDownload => "Partly downloaded",
            ModelState.NotInstalled => "Available",
            ModelState.Installed => "Installed",
            ModelState.UpdateAvailable => "Update available",
            ModelState.Custom => "Custom",
            _ => Entry.State.ToString(),
        },
    };

    /// <summary>Starts (or resumes) the download after confirmation.</summary>
    /// <returns>A task that completes when the download has started or was declined.</returns>
    [RelayCommand]
    private Task DownloadAsync() => _owner.DownloadAsync(this);

    /// <summary>Pauses the download.</summary>
    [RelayCommand]
    private void Pause() => Download?.Pause();

    /// <summary>Resumes the paused download.</summary>
    [RelayCommand]
    private void Resume() => Download?.Unpause();

    /// <summary>Cancels the download; the partial file is kept.</summary>
    [RelayCommand]
    private void Cancel() => Download?.Cancel();

    /// <summary>Re-hashes the installed file.</summary>
    /// <returns>A task that completes when verified.</returns>
    [RelayCommand(CanExecute = nameof(CanVerify))]
    private Task VerifyAsync() => _owner.VerifyAsync(this);

    /// <summary>Deletes the model after confirmation.</summary>
    /// <returns>A task that completes when deleted or declined.</returns>
    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    /// <summary>Deletes a leftover partial download.</summary>
    /// <returns>A task that completes when deleted.</returns>
    [RelayCommand]
    private Task DiscardPartialAsync() => _owner.DiscardPartialAsync(this);

    /// <summary>Called when the download's state changed (on the UI thread).</summary>
    internal void RefreshDownload()
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsTransferActive));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDiscardPartial));
        OnPropertyChanged(nameof(CanVerify));
        VerifyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
    }

    private long? InstalledSize()
    {
        if (Entry.Installed?.Path is not { } path)
        {
            return null;
        }

        var file = new FileInfo(path);
        return file.Exists ? file.Length : null;
    }
}
