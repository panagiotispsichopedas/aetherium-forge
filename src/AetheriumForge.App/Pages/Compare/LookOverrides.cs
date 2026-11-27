using System.Collections.Immutable;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The level a look is set at (IMPLEMENTATION_GUIDE §8, "Scopes"): All → Group → This texture.
/// </summary>
public enum LookScope
{
    /// <summary>The top-level look; applies to every group.</summary>
    All,

    /// <summary>The group's look; replaces the controls it sets from All.</summary>
    Group,

    /// <summary>A per-texture override; replaces the controls it sets from the group.</summary>
    Texture,
}

/// <summary>
/// A partial look: only the controls set at one scope. Immutable; equal when the same controls have the same values.
/// </summary>
public sealed class LookOverrides : IEquatable<LookOverrides>
{
    private readonly ImmutableSortedDictionary<LookControl, double> _values;

    private LookOverrides(ImmutableSortedDictionary<LookControl, double> values) => _values = values;

    /// <summary>Gets the empty set (nothing set at this scope).</summary>
    public static LookOverrides Empty { get; } = new(ImmutableSortedDictionary<LookControl, double>.Empty);

    /// <summary>Gets the set controls and their values.</summary>
    public IReadOnlyDictionary<LookControl, double> Values => _values;

    /// <summary>Gets the number of set controls.</summary>
    public int Count => _values.Count;

    /// <summary>Gets whether nothing is set.</summary>
    public bool IsEmpty => _values.IsEmpty;

    /// <summary>Builds overrides from control values (Denoise is ignored; values are clamped to the control ranges).</summary>
    /// <param name="values">The values.</param>
    /// <returns>The overrides.</returns>
    public static LookOverrides From(IEnumerable<KeyValuePair<LookControl, double>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableSortedDictionary.CreateBuilder<LookControl, double>();
        foreach (var (control, value) in values)
        {
            if (control != LookControl.Denoise)
            {
                builder[control] = LookControls.RangeOf(control).Clamp(value);
            }
        }

        return builder.Count == 0 ? Empty : new LookOverrides(builder.ToImmutable());
    }

    /// <summary>Builds overrides that set every control to <paramref name="look"/>'s value (a preset), or only the non-neutral ones.</summary>
    /// <param name="look">The look.</param>
    /// <param name="onlyNonNeutral"><see langword="true"/> to keep only the controls that differ from neutral.</param>
    /// <returns>The overrides.</returns>
    public static LookOverrides FromLook(LookSettings look, bool onlyNonNeutral)
    {
        ArgumentNullException.ThrowIfNull(look);
        return From(LookControls.Editable
            .Select(control => KeyValuePair.Create(control, ValueOf(look, control)))
            .Where(pair => !onlyNonNeutral || pair.Value != LookControls.RangeOf(pair.Key).Neutral));
    }

    /// <summary>Reads one control of a full look.</summary>
    /// <param name="look">The look.</param>
    /// <param name="control">The control.</param>
    /// <returns>The value.</returns>
    public static double ValueOf(LookSettings look, LookControl control)
    {
        ArgumentNullException.ThrowIfNull(look);
        return control switch
        {
            LookControl.AiStrength => look.AiStrength,
            LookControl.SharpenAmount => look.Sharpen.Amount,
            LookControl.SharpenRadius => look.Sharpen.Radius,
            LookControl.Denoise => look.Denoise ?? 0.0,
            LookControl.Brightness => look.Brightness,
            LookControl.Contrast => look.Contrast,
            LookControl.Gamma => look.Gamma,
            LookControl.Saturation => look.Saturation,
            LookControl.Temperature => look.Temperature,
            LookControl.NormalStrength => look.NormalStrength,
            LookControl.SpecularScale => look.SpecularScale,
            _ => throw new ArgumentOutOfRangeException(nameof(control), control, "Unknown look control."),
        };
    }

    /// <summary>Gets whether <paramref name="control"/> is set.</summary>
    /// <param name="control">The control.</param>
    /// <returns><see langword="true"/> when set at this scope.</returns>
    public bool IsSet(LookControl control) => _values.ContainsKey(control);

    /// <summary>Gets the value of <paramref name="control"/> when set.</summary>
    /// <param name="control">The control.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when set.</returns>
    public bool TryGet(LookControl control, out double value) => _values.TryGetValue(control, out value);

    /// <summary>Returns a copy with <paramref name="control"/> set to <paramref name="value"/> (clamped).</summary>
    /// <param name="control">The control.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new overrides.</returns>
    public LookOverrides With(LookControl control, double value) =>
        control == LookControl.Denoise ? this : new LookOverrides(_values.SetItem(control, LookControls.RangeOf(control).Clamp(value)));

    /// <summary>Returns a copy without <paramref name="control"/>.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The new overrides.</returns>
    public LookOverrides Without(LookControl control) => IsSet(control) ? new LookOverrides(_values.Remove(control)) : this;

    /// <summary>Applies the set controls on top of <paramref name="baseLook"/>.</summary>
    /// <param name="baseLook">The inherited look.</param>
    /// <returns>The combined look.</returns>
    public LookSettings ApplyTo(LookSettings baseLook)
    {
        ArgumentNullException.ThrowIfNull(baseLook);
        var look = baseLook;
        foreach (var (control, value) in _values)
        {
            look = control switch
            {
                LookControl.AiStrength => look with { AiStrength = value },
                LookControl.SharpenAmount => look with { Sharpen = look.Sharpen with { Amount = value } },
                LookControl.SharpenRadius => look with { Sharpen = look.Sharpen with { Radius = value } },
                LookControl.Brightness => look with { Brightness = value },
                LookControl.Contrast => look with { Contrast = value },
                LookControl.Gamma => look with { Gamma = value },
                LookControl.Saturation => look with { Saturation = value },
                LookControl.Temperature => look with { Temperature = value },
                LookControl.NormalStrength => look with { NormalStrength = value },
                LookControl.SpecularScale => look with { SpecularScale = value },
                _ => look,
            };
        }

        return look;
    }

    /// <summary>Gets the values keyed by control name (for JSON).</summary>
    /// <returns>The values by name.</returns>
    public Dictionary<string, double> ToNamedValues() => _values.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Builds overrides from values keyed by control name (unknown names are ignored).</summary>
    /// <param name="values">The values by name.</param>
    /// <returns>The overrides.</returns>
    public static LookOverrides FromNamedValues(IReadOnlyDictionary<string, double>? values) =>
        values is null
            ? Empty
            : From(values
                .Where(pair => Enum.TryParse<LookControl>(pair.Key, ignoreCase: true, out _))
                .Select(pair => KeyValuePair.Create(Enum.Parse<LookControl>(pair.Key, ignoreCase: true), pair.Value)));

    /// <inheritdoc />
    public bool Equals(LookOverrides? other) => other is not null && _values.Count == other._values.Count && _values.All(pair => other.TryGet(pair.Key, out var value) && value.Equals(pair.Value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as LookOverrides);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (control, value) in _values)
        {
            hash.Add(control);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Resolves the effective look control by control: texture override, else group, else All, else neutral (§8).
/// </summary>
public static class LookLayers
{
    /// <summary>Resolves the effective look.</summary>
    /// <param name="all">The All layer.</param>
    /// <param name="group">The group layer.</param>
    /// <param name="texture">The texture layer.</param>
    /// <returns>The effective look.</returns>
    public static LookSettings Resolve(LookOverrides all, LookOverrides group, LookOverrides texture)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(texture);
        return texture.ApplyTo(group.ApplyTo(all.ApplyTo(LookSettings.Neutral)));
    }

    /// <summary>
    /// Gets the value a control inherits at <paramref name="scope"/> from the scopes above it, and where it comes from.
    /// </summary>
    /// <param name="scope">The scope being edited.</param>
    /// <param name="control">The control.</param>
    /// <param name="all">The All layer.</param>
    /// <param name="group">The group layer.</param>
    /// <returns>The inherited value and its source (<see langword="null"/> for the neutral default).</returns>
    public static (double Value, LookScope? Source) Inherited(LookScope scope, LookControl control, LookOverrides all, LookOverrides group)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(group);
        if (scope == LookScope.Texture && group.TryGet(control, out var fromGroup))
        {
            return (fromGroup, LookScope.Group);
        }

        if (scope != LookScope.All && all.TryGet(control, out var fromAll))
        {
            return (fromAll, LookScope.All);
        }

        return (LookControls.RangeOf(control).Neutral, null);
    }
}
