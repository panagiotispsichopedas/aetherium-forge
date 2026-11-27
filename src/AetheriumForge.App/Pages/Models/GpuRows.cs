using AetheriumForge.Core.Gpu;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetheriumForge.App.Pages.Models;

/// <summary>
/// One GPU adapter in the adapters list.
/// </summary>
/// <param name="Adapter">The adapter.</param>
/// <param name="IsSelected"><see langword="true"/> for the adapter <see cref="GpuProfiler.SelectAdapter()"/> picked.</param>
public sealed record GpuAdapterRow(GpuAdapterInfo Adapter, bool IsSelected)
{
    /// <summary>Gets the adapter name.</summary>
    public string Name => Adapter.Name;

    /// <summary>Gets the dedicated video memory as text.</summary>
    public string MemoryText => ModelFormatting.Bytes(Adapter.DedicatedVideoMemory);

    /// <summary>Gets the adapter kind: discrete, integrated or software.</summary>
    public string KindText => Adapter.IsSoftware ? "Software" : Adapter.IsIntegrated ? "Integrated" : "Discrete";

    /// <summary>Gets the driver version, or an em dash.</summary>
    public string DriverText => Adapter.DriverVersion ?? "—";

    /// <summary>Gets "Selected" for the selected adapter, otherwise empty.</summary>
    public string SelectedText => IsSelected ? "Selected" : string.Empty;
}

/// <summary>
/// The calibration state of one installed model family on the selected adapter.
/// </summary>
public sealed partial class GpuFamilyRow : ObservableObject
{
    private readonly ModelsViewModel _owner;

    /// <summary>Gets or sets the stored profile for this adapter and driver, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileText), nameof(CalibrateLabel), nameof(IsCalibrated))]
    private GpuProfile? _profile;

    /// <summary>Gets or sets a value indicating whether a profile exists for this hardware but an older driver.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileText), nameof(CalibrateLabel))]
    private bool _needsRecalibration;

    /// <summary>Gets or sets a value indicating whether Calibrate is enabled (no calibration or pipeline run in progress).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
    private bool _canCalibrate;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuFamilyRow"/> class.
    /// </summary>
    /// <param name="owner">The page.</param>
    /// <param name="family">The model family.</param>
    /// <param name="variants">The installed variants (FP32 and/or FP16).</param>
    internal GpuFamilyRow(ModelsViewModel owner, string family, IReadOnlyList<Core.Imaging.ModelInfo> variants)
    {
        _owner = owner;
        Family = family;
        Variants = variants;
    }

    /// <summary>Gets the model family.</summary>
    public string Family { get; }

    /// <summary>Gets the installed variants.</summary>
    public IReadOnlyList<Core.Imaging.ModelInfo> Variants { get; }

    /// <summary>Gets the installed precisions as text, for example <c>FP32 + FP16</c>.</summary>
    public string VariantsText => string.Join(" + ", Variants.Select(variant => ModelFormatting.Precision(variant.Precision)).Distinct(StringComparer.Ordinal));

    /// <summary>Gets a value indicating whether a valid profile is stored.</summary>
    public bool IsCalibrated => Profile is not null;

    /// <summary>Gets the profile as text.</summary>
    public string ProfileText => Profile is { } profile
        ? $"{ModelFormatting.Precision(profile.Precision)}, tile {profile.TileSize}, {ModelFormatting.Number(profile.SecondsPerMegapixel, "0.00")} s/MP, calibrated {profile.CalibratedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
        : NeedsRecalibration ? "Recalibration needed (driver changed)" : "Not calibrated";

    /// <summary>Gets the calibrate button label.</summary>
    public string CalibrateLabel => Profile is null && !NeedsRecalibration ? "Calibrate" : "Recalibrate";

    /// <summary>Calibrates this family.</summary>
    /// <returns>A task that completes when the calibration ended.</returns>
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private Task CalibrateAsync() => _owner.CalibrateAsync(this);
}
