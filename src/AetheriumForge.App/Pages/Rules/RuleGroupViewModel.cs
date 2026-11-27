using System.ComponentModel;
using System.Globalization;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// One editable group on the Rules page. Numbers are edited as text so a half-typed value shows an inline error instead of being rejected.
/// The group's <see cref="Look"/> is carried through unchanged; it is edited on the Compare page.
/// </summary>
public sealed partial class RuleGroupViewModel : ObservableObject
{
    private readonly bool _initialised;

    private static readonly string[] _displayOnly = [nameof(Count), nameof(CountText), nameof(Error), nameof(HasError), nameof(LookSummary), nameof(PickedModel)];

    /// <summary>Initialises the editor from <paramref name="rule"/>.</summary>
    /// <param name="rule">The rule to edit.</param>
    public RuleGroupViewModel(GroupRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Name = rule.Name;
        PatternsText = string.Join(Environment.NewLine, rule.Match);
        MinSourceMaxText = Format(rule.MinSourceMax);
        MaxSourceMaxText = Format(rule.MaxSourceMax);
        CapText = rule.Cap.ToString(CultureInfo.InvariantCulture);
        MaxFactorText = rule.MaxFactor.ToString(CultureInfo.InvariantCulture);
        Model = rule.Model;
        NormalStrengthText = rule.NormalStrength.ToString("0.###", CultureInfo.InvariantCulture);
        Look = rule.Look;
        Kinds = [.. Enum.GetValues<TextureKind>().Select(k => new KindOption(k, rule.Kinds?.Contains(k) == true))];
        foreach (var option in Kinds)
        {
            option.PropertyChanged += (_, _) => OnEdited();
        }

        TryBuild(out string? error);
        Error = error;
        _initialised = true;
    }

    /// <summary>Raised after any edit of a value that ends up in the rule.</summary>
    public event EventHandler? Edited;

    /// <summary>Gets or sets the group name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Gets or sets the match patterns, one per line.</summary>
    [ObservableProperty]
    public partial string PatternsText { get; set; }

    /// <summary>Gets or sets the inclusive lower bound on the source max dimension; empty for none.</summary>
    [ObservableProperty]
    public partial string MinSourceMaxText { get; set; }

    /// <summary>Gets or sets the inclusive upper bound on the source max dimension; empty for none.</summary>
    [ObservableProperty]
    public partial string MaxSourceMaxText { get; set; }

    /// <summary>Gets or sets the output cap in pixels.</summary>
    [ObservableProperty]
    public partial string CapText { get; set; }

    /// <summary>Gets or sets the maximum scale factor.</summary>
    [ObservableProperty]
    public partial string MaxFactorText { get; set; }

    /// <summary>Gets or sets the model name (free text; <see cref="PickedModel"/> fills it from the installed list).</summary>
    [ObservableProperty]
    public partial string Model { get; set; }

    /// <summary>Gets or sets the normal-map strength.</summary>
    [ObservableProperty]
    public partial string NormalStrengthText { get; set; }

    /// <summary>Gets or sets a model picked from the installed list; picking copies it into <see cref="Model"/>.</summary>
    [ObservableProperty]
    public partial string? PickedModel { get; set; }

    /// <summary>Gets the live number of textures this group upscales, or <see langword="null"/> before the first count.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int? Count { get; set; }

    /// <summary>Gets the first problem with this group, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Gets the kinds; none selected means every kind.</summary>
    public IReadOnlyList<KindOption> Kinds { get; }

    /// <summary>Gets the look, carried through unchanged (edited on Compare).</summary>
    public LookSettings? Look { get; private set; }

    /// <summary>Gets the look summary: <c>Neutral</c> or the changed controls.</summary>
    public string LookSummary => DescribeLook(Look);

    /// <summary>Gets the count for the list, for example <c>1,234</c>.</summary>
    public string CountText => Count is int count ? count.ToString("N0", CultureInfo.CurrentCulture) : "…";

    /// <summary>Gets a value indicating whether the group has a problem.</summary>
    public bool HasError => Error is not null;

    /// <summary>Describes a look: <c>Neutral</c>, or the controls that differ from neutral.</summary>
    /// <param name="look">The look, or <see langword="null"/> for neutral.</param>
    /// <returns>The summary.</returns>
    public static string DescribeLook(LookSettings? look)
    {
        if (look is null || look.IsNeutral)
        {
            return "Neutral";
        }

        var neutral = LookSettings.Neutral;
        var parts = new List<string>();
        Add(parts, "AI strength", look.AiStrength, neutral.AiStrength);
        if (look.Sharpen != neutral.Sharpen)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Sharpen {look.Sharpen.Amount:0.##} (r {look.Sharpen.Radius:0.##})"));
        }

        if (look.Denoise is double denoise)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Denoise {denoise:0.##}"));
        }

        Add(parts, "Brightness", look.Brightness, neutral.Brightness);
        Add(parts, "Contrast", look.Contrast, neutral.Contrast);
        Add(parts, "Gamma", look.Gamma, neutral.Gamma);
        Add(parts, "Saturation", look.Saturation, neutral.Saturation);
        Add(parts, "Temperature", look.Temperature, neutral.Temperature);
        Add(parts, "Normal strength", look.NormalStrength, neutral.NormalStrength);
        Add(parts, "Specular", look.SpecularScale, neutral.SpecularScale);
        return string.Join(", ", parts);
    }

    /// <summary>Replaces the look (when the Compare page changed it in the project).</summary>
    /// <param name="look">The new look.</param>
    public void SetLook(LookSettings? look)
    {
        if (Equals(Look, look))
        {
            return;
        }

        Look = look;
        OnPropertyChanged(nameof(LookSummary));
    }

    /// <summary>
    /// Builds the rule from the edited values.
    /// </summary>
    /// <param name="error">The first problem, or <see langword="null"/>.</param>
    /// <returns>The rule, or <see langword="null"/> when a value is not a number.</returns>
    public GroupRule? TryBuild(out string? error)
    {
        string label = string.IsNullOrWhiteSpace(Name) ? "Group" : $"Group '{Name.Trim()}'";
        if (!TryParseOptional(MinSourceMaxText, out int? minSource))
        {
            error = $"{label}: the minimum size must be a whole number or empty.";
            return null;
        }

        if (!TryParseOptional(MaxSourceMaxText, out int? maxSource))
        {
            error = $"{label}: the maximum size must be a whole number or empty.";
            return null;
        }

        if (!int.TryParse(CapText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap))
        {
            error = $"{label}: the cap must be a whole number.";
            return null;
        }

        if (!int.TryParse(MaxFactorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxFactor))
        {
            error = $"{label}: the max factor must be a whole number.";
            return null;
        }

        if (!double.TryParse(NormalStrengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out double normalStrength) || !double.IsFinite(normalStrength))
        {
            error = $"{label}: the normal strength must be a number.";
            return null;
        }

        var kinds = Kinds.Where(k => k.IsSelected).Select(k => k.Kind).ToArray();
        var rule = new GroupRule
        {
            Name = Name?.Trim() ?? string.Empty,
            Match = [.. SplitLines(PatternsText)],
            MinSourceMax = minSource,
            MaxSourceMax = maxSource,
            Kinds = kinds.Length == 0 ? null : kinds,
            Cap = cap,
            MaxFactor = maxFactor,
            Model = Model?.Trim() ?? string.Empty,
            NormalStrength = normalStrength,
            Look = Look,
        };

        // The same checks ForgeRules runs on load, applied to this group alone.
        error = RulesApplier.Validate(new ForgeRules { Groups = [rule], MinSource = 0, Exclude = [] });
        return rule;
    }

    /// <summary>Splits multi-line text into trimmed, non-empty lines.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines.</returns>
    internal static IEnumerable<string> SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPropertyChanged(e);
        if (_initialised && e.PropertyName is { } name && !_displayOnly.Contains(name))
        {
            OnEdited();
        }
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryParseOptional(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static void Add(List<string> parts, string label, double value, double neutral)
    {
        if (!value.Equals(neutral))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {value:0.##}"));
        }
    }

    partial void OnPickedModelChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Model = value;
        }
    }

    private void OnEdited()
    {
        TryBuild(out string? error);
        Error = error;
        Edited?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// One texture kind check box in the group editor.
/// </summary>
/// <param name="kind">The kind.</param>
/// <param name="isSelected">Whether it starts checked.</param>
public sealed partial class KindOption(TextureKind kind, bool isSelected) : ObservableObject
{
    /// <summary>Gets the kind.</summary>
    public TextureKind Kind { get; } = kind;

    /// <summary>Gets or sets a value indicating whether the group applies to this kind.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}
