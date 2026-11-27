using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The level a look is set at (IMPLEMENTATION_GUIDE §8, "Scopes"): All → Group → This texture.
/// </summary>
public enum LookScope
{
    /// <summary>The top-level look (<see cref="ForgeRules.Look"/>); applies to every group.</summary>
    All,

    /// <summary>The group's look (<see cref="GroupRule.Look"/>); replaces the controls it sets from All.</summary>
    Group,

    /// <summary>A per-texture override (the <c>texture_look</c> table); replaces the controls it sets from the group.</summary>
    Texture,
}

/// <summary>
/// Reads and edits Core's partial <see cref="LookOverrides"/> by <see cref="LookControl"/>, for the Look panel's slider rows.
/// </summary>
public static class LookOverridesExtensions
{
    /// <summary>Gets the value <paramref name="look"/> sets for <paramref name="control"/>, or <see langword="null"/> when unset.</summary>
    /// <param name="look">The overrides.</param>
    /// <param name="control">The control.</param>
    /// <returns>The set value, or <see langword="null"/>.</returns>
    public static double? Get(this LookOverrides look, LookControl control)
    {
        ArgumentNullException.ThrowIfNull(look);
        return control switch
        {
            LookControl.AiStrength => look.AiStrength,
            LookControl.SharpenAmount => look.SharpenAmount,
            LookControl.SharpenRadius => look.SharpenRadius,
            LookControl.Denoise => look.Denoise,
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

    /// <summary>Gets whether <paramref name="look"/> sets <paramref name="control"/>.</summary>
    /// <param name="look">The overrides.</param>
    /// <param name="control">The control.</param>
    /// <returns><see langword="true"/> when set.</returns>
    public static bool IsSet(this LookOverrides look, LookControl control) => look.Get(control) is not null;

    /// <summary>Returns a copy that sets <paramref name="control"/> to <paramref name="value"/> (clamped to the slider range).</summary>
    /// <param name="look">The overrides.</param>
    /// <param name="control">The control.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new overrides.</returns>
    public static LookOverrides With(this LookOverrides look, LookControl control, double value) =>
        control == LookControl.Denoise ? look : look.Set(control, LookControls.RangeOf(control).Clamp(value));

    /// <summary>Returns a copy that no longer sets <paramref name="control"/>.</summary>
    /// <param name="look">The overrides.</param>
    /// <param name="control">The control.</param>
    /// <returns>The new overrides.</returns>
    public static LookOverrides Without(this LookOverrides look, LookControl control) => look.Set(control, null);

    /// <summary>Gets the controls <paramref name="look"/> sets, with their values.</summary>
    /// <param name="look">The overrides.</param>
    /// <returns>The set controls.</returns>
    public static IReadOnlyDictionary<LookControl, double> SetControls(this LookOverrides look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var values = new Dictionary<LookControl, double>();
        foreach (var control in LookControls.Editable)
        {
            if (look.Get(control) is { } value)
            {
                values[control] = value;
            }
        }

        return values;
    }

    /// <summary>Gets the value of <paramref name="control"/> in a full look.</summary>
    /// <param name="look">The look.</param>
    /// <param name="control">The control.</param>
    /// <returns>The value.</returns>
    public static double ValueOf(this LookSettings look, LookControl control) => LookOverrides.From(look).Get(control) ?? LookControls.RangeOf(control).Neutral;

    /// <summary>Returns <see langword="null"/> for an empty set (Core stores "nothing set" as no look block).</summary>
    /// <param name="look">The overrides.</param>
    /// <returns>The overrides, or <see langword="null"/> when empty.</returns>
    public static LookOverrides? NullIfEmpty(this LookOverrides look) => look is null || look.IsEmpty ? null : look;

    private static LookOverrides Set(this LookOverrides look, LookControl control, double? value)
    {
        ArgumentNullException.ThrowIfNull(look);
        return control switch
        {
            LookControl.AiStrength => look with { AiStrength = value },
            LookControl.SharpenAmount => look with { SharpenAmount = value },
            LookControl.SharpenRadius => look with { SharpenRadius = value },
            LookControl.Denoise => look with { Denoise = value },
            LookControl.Brightness => look with { Brightness = value },
            LookControl.Contrast => look with { Contrast = value },
            LookControl.Gamma => look with { Gamma = value },
            LookControl.Saturation => look with { Saturation = value },
            LookControl.Temperature => look with { Temperature = value },
            LookControl.NormalStrength => look with { NormalStrength = value },
            LookControl.SpecularScale => look with { SpecularScale = value },
            _ => throw new ArgumentOutOfRangeException(nameof(control), control, "Unknown look control."),
        };
    }
}

/// <summary>
/// The Look panel's view of Core's <see cref="LookResolver"/>: the effective look, and what a control inherits at a scope.
/// </summary>
public static class LookLayers
{
    /// <summary>Resolves the effective look (texture → group → All → neutral) with <see cref="LookResolver.ResolveScopes"/>.</summary>
    /// <param name="all">The All layer.</param>
    /// <param name="group">The group layer.</param>
    /// <param name="texture">The texture layer.</param>
    /// <returns>The effective look.</returns>
    public static LookSettings Resolve(LookOverrides? all, LookOverrides? group, LookOverrides? texture) => LookResolver.ResolveScopes(all, group, texture);

    /// <summary>
    /// Gets the value a control inherits at <paramref name="scope"/> from the scopes above it (resolved by <see cref="LookResolver"/>), and
    /// where it comes from.
    /// </summary>
    /// <param name="scope">The scope being edited.</param>
    /// <param name="control">The control.</param>
    /// <param name="all">The All layer.</param>
    /// <param name="group">The group layer.</param>
    /// <returns>The inherited value and its source (<see langword="null"/> for the neutral default).</returns>
    public static (double Value, LookScope? Source) Inherited(LookScope scope, LookControl control, LookOverrides? all, LookOverrides? group)
    {
        var inherited = scope switch
        {
            LookScope.Texture => LookResolver.ResolveScopes(all, group, null),
            LookScope.Group => LookResolver.ResolveScopes(all, null, null),
            _ => LookSettings.Neutral,
        };

        LookScope? source = scope switch
        {
            LookScope.Texture when group?.IsSet(control) == true => LookScope.Group,
            LookScope.Texture or LookScope.Group when all?.IsSet(control) == true => LookScope.All,
            _ => null,
        };

        return (inherited.ValueOf(control), source);
    }
}
