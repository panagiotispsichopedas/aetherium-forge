using System.Globalization;
using AetheriumForge.Core.Jobs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// One state in the Queue's state multi-select, with the number of jobs in that state under the other filters.
/// </summary>
public sealed partial class StateFilterOption : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _silent;

    /// <summary>Initialises the option, selected.</summary>
    /// <param name="state">The state.</param>
    /// <param name="selectionChanged">Called after <see cref="IsSelected"/> changed.</param>
    public StateFilterOption(JobState state, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(selectionChanged);

        State = state;
        _selectionChanged = selectionChanged;
        SetSelectedSilently(true);
    }

    /// <summary>Gets the state.</summary>
    public JobState State { get; }

    /// <summary>Gets or sets a value indicating whether jobs in this state are shown.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets the number of jobs in this state under the group, kind and text filters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; internal set; }

    /// <summary>Gets the checkbox label, for example <c>Done (143)</c>.</summary>
    public string Label => $"{State} ({Count.ToString("N0", CultureInfo.CurrentCulture)})";

    /// <summary>Sets <see cref="IsSelected"/> without notifying the owner (used for "select all").</summary>
    /// <param name="value">The new value.</param>
    /// <returns><see langword="true"/> when the value changed.</returns>
    internal bool SetSelectedSilently(bool value)
    {
        if (IsSelected == value)
        {
            return false;
        }

        _silent = true;
        try
        {
            IsSelected = value;
        }
        finally
        {
            _silent = false;
        }

        return true;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_silent)
        {
            _selectionChanged();
        }
    }
}
