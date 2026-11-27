using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// One slider row of the Look panel: the value at the edited scope, whether it is set there, and the inherited value it falls back to.
/// </summary>
public sealed partial class LookControlViewModel : ObservableObject
{
    private readonly LookPanelViewModel _owner;
    private double _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookControlViewModel"/> class.
    /// </summary>
    /// <param name="owner">The Look panel.</param>
    /// <param name="control">The control.</param>
    internal LookControlViewModel(LookPanelViewModel owner, LookControl control)
    {
        _owner = owner;
        Control = control;
        var range = LookControls.RangeOf(control);
        Label = range.Label;
        Minimum = range.Minimum;
        Maximum = range.Maximum;
        Step = range.Step;
        _value = range.Neutral;
        InheritedValue = range.Neutral;
    }

    /// <summary>Gets the control.</summary>
    public LookControl Control { get; }

    /// <summary>Gets the label.</summary>
    public string Label { get; }

    /// <summary>Gets the accessible name of the reset button.</summary>
    public string ResetName => $"Reset {Label}";

    /// <summary>Gets the slider minimum.</summary>
    public double Minimum { get; }

    /// <summary>Gets the slider maximum.</summary>
    public double Maximum { get; }

    /// <summary>Gets the slider step.</summary>
    public double Step { get; }

    /// <summary>Gets or sets the value shown (set at this scope, else inherited); setting it sets the control at the edited scope.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!value.Equals(_value))
            {
                _owner.SetValue(Control, value);
            }
        }
    }

    /// <summary>Gets the value as text.</summary>
    public string ValueText => Format(Value);

    /// <summary>Gets or sets whether the control is set at the edited scope.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherited))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool IsSetHere { get; set; }

    /// <summary>Gets whether the control falls back to an inherited value.</summary>
    public bool IsInherited => !IsSetHere;

    /// <summary>Gets or sets the value the control inherits from the scopes above.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InheritedText))]
    public partial double InheritedValue { get; set; }

    /// <summary>Gets or sets where the inherited value comes from: "inherited from Group", "inherited from All" or "default".</summary>
    [ObservableProperty]
    public partial string InheritedFrom { get; set; } = "default";

    /// <summary>Gets the inherited value with its source, for the marker tooltip.</summary>
    public string InheritedText => $"{InheritedFrom}: {Format(InheritedValue)}";

    /// <summary>Gets or sets whether the control applies to the sample's kind.</summary>
    [ObservableProperty]
    public partial bool IsApplicable { get; set; } = true;

    /// <summary>Gets or sets the tooltip (why it is disabled, or what it does).</summary>
    [ObservableProperty]
    public partial string? Tooltip { get; set; }

    /// <summary>Clears the control at the edited scope, so it falls back to the inherited value.</summary>
    [RelayCommand(CanExecute = nameof(IsSetHere))]
    public void Reset() => _owner.ResetControl(Control);

    /// <summary>Updates the row from the panel's layers (no change is reported back).</summary>
    /// <param name="value">The shown value.</param>
    /// <param name="isSetHere">Whether it is set at the edited scope.</param>
    /// <param name="inherited">The inherited value.</param>
    /// <param name="source">Where the inherited value comes from.</param>
    internal void Update(double value, bool isSetHere, double inherited, LookScope? source)
    {
        if (!value.Equals(_value))
        {
            _value = value;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
        }

        IsSetHere = isSetHere;
        InheritedValue = inherited;
        InheritedFrom = source switch
        {
            LookScope.Group => "inherited from Group",
            LookScope.All => "inherited from All",
            _ => "default",
        };
    }

    private string Format(double value) => Control switch
    {
        LookControl.Brightness or LookControl.Temperature => value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture),
        LookControl.SharpenRadius => $"{value.ToString("F1", CultureInfo.CurrentCulture)} px",
        _ => value.ToString("F2", CultureInfo.CurrentCulture),
    };
}
