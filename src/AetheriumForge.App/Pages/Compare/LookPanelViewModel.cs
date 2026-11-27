using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The Look panel: one slider per <see cref="LookSettings"/> control, with per-kind applicability (IMPLEMENTATION_GUIDE §8).
/// </summary>
public sealed partial class LookPanelViewModel : ObservableObject
{
    private int _suppressChanged;

    /// <summary>Initializes a new instance of the <see cref="LookPanelViewModel"/> class with the neutral look.</summary>
    public LookPanelViewModel()
    {
        Load(LookSettings.Neutral);
        UpdateApplicability();
    }

    /// <summary>Raised when the user changed any control (not when a whole look is loaded with <see cref="Load"/>).</summary>
    public event EventHandler? LookChanged;

    /// <summary>Gets or sets the AI strength (0–1).</summary>
    [ObservableProperty]
    public partial double AiStrength { get; set; }

    /// <summary>Gets or sets the sharpen amount.</summary>
    [ObservableProperty]
    public partial double SharpenAmount { get; set; }

    /// <summary>Gets or sets the sharpen radius.</summary>
    [ObservableProperty]
    public partial double SharpenRadius { get; set; }

    /// <summary>Gets or sets the brightness (−1–1).</summary>
    [ObservableProperty]
    public partial double Brightness { get; set; }

    /// <summary>Gets or sets the contrast (0.5–2).</summary>
    [ObservableProperty]
    public partial double Contrast { get; set; }

    /// <summary>Gets or sets the gamma (0.5–2).</summary>
    [ObservableProperty]
    public partial double Gamma { get; set; }

    /// <summary>Gets or sets the saturation (0–2).</summary>
    [ObservableProperty]
    public partial double Saturation { get; set; }

    /// <summary>Gets or sets the temperature (−1–1).</summary>
    [ObservableProperty]
    public partial double Temperature { get; set; }

    /// <summary>Gets or sets the normal strength.</summary>
    [ObservableProperty]
    public partial double NormalStrength { get; set; }

    /// <summary>Gets or sets the specular scale (0–2).</summary>
    [ObservableProperty]
    public partial double SpecularScale { get; set; }

    /// <summary>Gets or sets the kind of the sample being previewed; decides which controls apply.</summary>
    [ObservableProperty]
    public partial TextureKind Kind { get; set; } = TextureKind.Diffuse;

    /// <summary>Gets or sets whether the sample runs through an AI model.</summary>
    [ObservableProperty]
    public partial bool UsesModel { get; set; } = true;

    /// <summary>Gets the slider ranges, keyed by control (for the view).</summary>
    public IReadOnlyDictionary<LookControl, LookControlRange> Ranges { get; } = LookControls.Ranges;

    /// <summary>Gets whether the AI-strength slider applies.</summary>
    public bool IsAiStrengthEnabled => IsEnabled(LookControl.AiStrength);

    /// <summary>Gets whether the sharpen sliders apply.</summary>
    public bool IsSharpenEnabled => IsEnabled(LookControl.SharpenAmount);

    /// <summary>Gets whether the colour-grading sliders apply.</summary>
    public bool IsColourEnabled => IsEnabled(LookControl.Brightness);

    /// <summary>Gets whether the normal-strength slider applies.</summary>
    public bool IsNormalStrengthEnabled => IsEnabled(LookControl.NormalStrength);

    /// <summary>Gets whether the specular slider applies.</summary>
    public bool IsSpecularEnabled => IsEnabled(LookControl.SpecularScale);

    /// <summary>Gets whether the denoise slider applies (never yet: it needs a denoise model).</summary>
    public bool IsDenoiseEnabled => IsEnabled(LookControl.Denoise);

    /// <summary>Gets the AI-strength tooltip.</summary>
    public string? AiStrengthTooltip => LookControls.DisabledReason(LookControl.AiStrength, Kind, UsesModel) ?? "1 = AI only, 0 = plain Lanczos. Lower it to tame painted-looking detail.";

    /// <summary>Gets the sharpen tooltip.</summary>
    public string? SharpenTooltip => LookControls.DisabledReason(LookControl.SharpenAmount, Kind, UsesModel) ?? "Unsharp mask after resizing; 0 = off.";

    /// <summary>Gets the colour-grading tooltip.</summary>
    public string? ColourTooltip => LookControls.DisabledReason(LookControl.Brightness, Kind, UsesModel) ?? "Colour grading in linear light (colour maps only).";

    /// <summary>Gets the normal-strength tooltip.</summary>
    public string? NormalStrengthTooltip => LookControls.DisabledReason(LookControl.NormalStrength, Kind, UsesModel) ?? "How 3D: scales the relief of the normal map.";

    /// <summary>Gets the specular tooltip.</summary>
    public string? SpecularTooltip => LookControls.DisabledReason(LookControl.SpecularScale, Kind, UsesModel) ?? "How shiny: scales specular intensity.";

    /// <summary>Gets the denoise tooltip.</summary>
    public string DenoiseTooltip { get; } = LookControls.DenoiseTooltip;

    /// <summary>Gets whether <paramref name="control"/> applies to the current sample.</summary>
    /// <param name="control">The control.</param>
    /// <returns><see langword="true"/> when it applies.</returns>
    public bool IsEnabled(LookControl control) => LookControls.AppliesTo(control, Kind, UsesModel);

    /// <summary>Builds the look from the sliders.</summary>
    /// <returns>The look.</returns>
    public LookSettings ToSettings() => new()
    {
        AiStrength = Range(LookControl.AiStrength).Clamp(AiStrength),
        Sharpen = new SharpenSettings(Range(LookControl.SharpenAmount).Clamp(SharpenAmount), Range(LookControl.SharpenRadius).Clamp(SharpenRadius)),
        Brightness = Range(LookControl.Brightness).Clamp(Brightness),
        Contrast = Range(LookControl.Contrast).Clamp(Contrast),
        Gamma = Range(LookControl.Gamma).Clamp(Gamma),
        Saturation = Range(LookControl.Saturation).Clamp(Saturation),
        Temperature = Range(LookControl.Temperature).Clamp(Temperature),
        NormalStrength = Range(LookControl.NormalStrength).Clamp(NormalStrength),
        SpecularScale = Range(LookControl.SpecularScale).Clamp(SpecularScale),
    };

    /// <summary>Sets every slider from <paramref name="look"/> and raises <see cref="LookChanged"/> once when <paramref name="notify"/> is set.</summary>
    /// <param name="look">The look; <see langword="null"/> means neutral.</param>
    /// <param name="notify"><see langword="true"/> to raise <see cref="LookChanged"/> (a preset); <see langword="false"/> for a silent load.</param>
    public void Load(LookSettings? look, bool notify = false)
    {
        look ??= LookSettings.Neutral;
        _suppressChanged++;
        try
        {
            AiStrength = look.AiStrength;
            SharpenAmount = look.Sharpen.Amount;
            SharpenRadius = look.Sharpen.Radius;
            Brightness = look.Brightness;
            Contrast = look.Contrast;
            Gamma = look.Gamma;
            Saturation = look.Saturation;
            Temperature = look.Temperature;
            NormalStrength = look.NormalStrength;
            SpecularScale = look.SpecularScale;
        }
        finally
        {
            _suppressChanged--;
        }

        if (notify)
        {
            LookChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Kind) or nameof(UsesModel):
                UpdateApplicability();
                break;
            case nameof(AiStrength) or nameof(SharpenAmount) or nameof(SharpenRadius) or nameof(Brightness) or nameof(Contrast) or nameof(Gamma)
                or nameof(Saturation) or nameof(Temperature) or nameof(NormalStrength) or nameof(SpecularScale):
                if (_suppressChanged == 0)
                {
                    LookChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
        }
    }

    private static LookControlRange Range(LookControl control) => LookControls.RangeOf(control);

    private void UpdateApplicability()
    {
        OnPropertyChanged(nameof(IsAiStrengthEnabled));
        OnPropertyChanged(nameof(IsSharpenEnabled));
        OnPropertyChanged(nameof(IsColourEnabled));
        OnPropertyChanged(nameof(IsNormalStrengthEnabled));
        OnPropertyChanged(nameof(IsSpecularEnabled));
        OnPropertyChanged(nameof(IsDenoiseEnabled));
        OnPropertyChanged(nameof(AiStrengthTooltip));
        OnPropertyChanged(nameof(SharpenTooltip));
        OnPropertyChanged(nameof(ColourTooltip));
        OnPropertyChanged(nameof(NormalStrengthTooltip));
        OnPropertyChanged(nameof(SpecularTooltip));
    }
}
