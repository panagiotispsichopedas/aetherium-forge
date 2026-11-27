using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The Look panel (IMPLEMENTATION_GUIDE §8 and §14 "Look panel behaviour"): one slider per control, editing one scope
/// (All / This group / This texture) at a time. Each scope keeps a saved layer and a working layer; the effective look resolves
/// texture → group → All → neutral, control by control.
/// </summary>
public sealed partial class LookPanelViewModel : ObservableObject
{
    private readonly Dictionary<LookScope, LookOverrides> _saved = new()
    {
        [LookScope.All] = LookOverrides.Empty,
        [LookScope.Group] = LookOverrides.Empty,
        [LookScope.Texture] = LookOverrides.Empty,
    };

    private readonly Dictionary<LookScope, LookOverrides> _working = new()
    {
        [LookScope.All] = LookOverrides.Empty,
        [LookScope.Group] = LookOverrides.Empty,
        [LookScope.Texture] = LookOverrides.Empty,
    };

    private readonly Dictionary<LookControl, LookControlViewModel> _rows;

    /// <summary>Initializes a new instance of the <see cref="LookPanelViewModel"/> class with every scope empty (neutral).</summary>
    public LookPanelViewModel()
    {
        _rows = LookControls.Editable.ToDictionary(control => control, control => new LookControlViewModel(this, control));
        Controls = [.. LookControls.Editable.Select(control => _rows[control])];
        DetailControls = [.. Controls.Where(row => LookControls.SectionOf(row.Control) == "Detail")];
        ColourControls = [.. Controls.Where(row => LookControls.SectionOf(row.Control) == "Colour")];
        SurfaceControls = [.. Controls.Where(row => LookControls.SectionOf(row.Control) == "Surface")];
        Refresh();
    }

    /// <summary>Raised when the working look changed (a slider, a reset, a preset, an undo or a revert); never on a silent load.</summary>
    public event EventHandler? LookChanged;

    /// <summary>Gets every slider row.</summary>
    public IReadOnlyList<LookControlViewModel> Controls { get; }

    /// <summary>Gets the Detail section rows.</summary>
    public IReadOnlyList<LookControlViewModel> DetailControls { get; }

    /// <summary>Gets the Colour section rows.</summary>
    public IReadOnlyList<LookControlViewModel> ColourControls { get; }

    /// <summary>Gets the Surface section rows.</summary>
    public IReadOnlyList<LookControlViewModel> SurfaceControls { get; }

    /// <summary>Gets the scopes, for the selector.</summary>
    public IReadOnlyList<LookScope> Scopes { get; } = Enum.GetValues<LookScope>();

    /// <summary>Gets or sets the scope the sliders edit.</summary>
    [ObservableProperty]
    public partial LookScope Scope { get; set; } = LookScope.Group;

    /// <summary>Gets or sets the kind of the sample being previewed; decides which controls apply.</summary>
    [ObservableProperty]
    public partial TextureKind Kind { get; set; } = TextureKind.Diffuse;

    /// <summary>Gets or sets whether the sample runs through an AI model.</summary>
    [ObservableProperty]
    public partial bool UsesModel { get; set; } = true;

    /// <summary>Gets whether the denoise slider applies (never yet: it needs a denoise model).</summary>
    public bool IsDenoiseEnabled => LookControls.AppliesTo(LookControl.Denoise, Kind, UsesModel);

    /// <summary>Gets the denoise tooltip.</summary>
    public string DenoiseTooltip { get; } = LookControls.DenoiseTooltip;

    /// <summary>Gets whether any scope has unsaved changes.</summary>
    public bool IsDirty => _working.Any(pair => !pair.Value.Equals(_saved[pair.Key]));

    /// <summary>Gets whether the edited scope has unsaved changes.</summary>
    public bool IsScopeDirty => !_working[Scope].Equals(_saved[Scope]);

    /// <summary>Gets the working overrides of the edited scope.</summary>
    public LookOverrides CurrentOverrides => _working[Scope];

    /// <summary>Gets the row of <paramref name="control"/>.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The row.</returns>
    public LookControlViewModel this[LookControl control] => _rows[control];

    /// <summary>Gets the working overrides of a scope.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The overrides.</returns>
    public LookOverrides Working(LookScope scope) => _working[scope];

    /// <summary>Gets the saved overrides of a scope.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The overrides.</returns>
    public LookOverrides Saved(LookScope scope) => _saved[scope];

    /// <summary>Gets whether <paramref name="control"/> applies to the current sample.</summary>
    /// <param name="control">The control.</param>
    /// <returns><see langword="true"/> when it applies.</returns>
    public bool IsEnabled(LookControl control) => LookControls.AppliesTo(control, Kind, UsesModel);

    /// <summary>Gets the effective look (all working layers resolved).</summary>
    /// <returns>The look.</returns>
    public LookSettings ToSettings() => LookLayers.Resolve(_working[LookScope.All], _working[LookScope.Group], _working[LookScope.Texture]);

    /// <summary>Loads a scope's stored overrides as both saved and working (drops unsaved edits of that scope); raises no change.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="overrides">The stored overrides.</param>
    public void LoadLayer(LookScope scope, LookOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _saved[scope] = overrides;
        _working[scope] = overrides;
        Refresh();
    }

    /// <summary>Sets a control at the edited scope.</summary>
    /// <param name="control">The control.</param>
    /// <param name="value">The value (clamped to the range).</param>
    public void SetValue(LookControl control, double value) => SetWorking(_working[Scope].With(control, value));

    /// <summary>Clears a control at the edited scope, so it falls back to the inherited value (neutral at All).</summary>
    /// <param name="control">The control.</param>
    public void ResetControl(LookControl control) => SetWorking(_working[Scope].Without(control));

    /// <summary>Sets every control of a preset at the edited scope.</summary>
    /// <param name="look">The preset look.</param>
    public void ApplyPreset(LookSettings look)
    {
        ArgumentNullException.ThrowIfNull(look);
        SetWorking(LookOverrides.From(look) with { Denoise = null });
    }

    /// <summary>Replaces the edited scope's working overrides (Undo).</summary>
    /// <param name="overrides">The overrides.</param>
    public void RestoreOverrides(LookOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        SetWorking(overrides);
    }

    /// <summary>Discards the edited scope's unsaved changes.</summary>
    public void Revert() => SetWorking(_saved[Scope]);

    /// <summary>Marks a scope's working overrides as saved (after Apply).</summary>
    /// <param name="scope">The scope.</param>
    public void MarkSaved(LookScope scope)
    {
        _saved[scope] = _working[scope];
        RaiseDirty();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Scope) or nameof(Kind) or nameof(UsesModel))
        {
            Refresh();
        }
    }

    private static string DescriptionOf(LookControl control) => control switch
    {
        LookControl.AiStrength => "1 = AI only, 0 = plain Lanczos. Lower it to tame painted-looking detail.",
        LookControl.SharpenAmount or LookControl.SharpenRadius => "Unsharp mask after resizing; 0 = off.",
        LookControl.NormalStrength => "Relief: how 3D the normal map looks.",
        LookControl.SpecularScale => "Shine: scales specular intensity.",
        _ => "Colour grading in linear light (colour maps only).",
    };

    private void SetWorking(LookOverrides overrides)
    {
        if (overrides.Equals(_working[Scope]))
        {
            return;
        }

        _working[Scope] = overrides;
        Refresh();
        LookChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        // OnPropertyChanged can run from property initialisers before the rows exist.
        if (_rows is null)
        {
            return;
        }

        var current = _working[Scope];
        foreach (var row in Controls)
        {
            var (inherited, source) = LookLayers.Inherited(Scope, row.Control, _working[LookScope.All], _working[LookScope.Group]);
            var own = current.Get(row.Control);
            var isSet = own is not null;
            row.Update(own ?? inherited, isSet, inherited, source);
            row.IsApplicable = IsEnabled(row.Control);
            row.Tooltip = LookControls.DisabledReason(row.Control, Kind, UsesModel) ?? DescriptionOf(row.Control);
        }

        OnPropertyChanged(nameof(IsDenoiseEnabled));
        RaiseDirty();
    }

    private void RaiseDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsScopeDirty));
        OnPropertyChanged(nameof(CurrentOverrides));
    }
}
