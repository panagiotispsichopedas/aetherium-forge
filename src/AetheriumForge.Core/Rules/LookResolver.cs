namespace AetheriumForge.Core.Rules;

/// <summary>
/// Resolves the effective look of a texture (IMPLEMENTATION_GUIDE §8, "Scopes"): control by control, the texture override wins, else
/// the group's look, else the rules' global (All) look, else neutral. This is the single place the scope order lives; the pipeline, the
/// CLI and the UI all resolve through it.
/// </summary>
public static class LookResolver
{
    /// <summary>
    /// Resolves the effective look for a texture in <paramref name="groupName"/>.
    /// </summary>
    /// <param name="rules">The rules (global look and groups).</param>
    /// <param name="groupName">The job's group, compared case-insensitively; <see langword="null"/> or an unknown group uses only the global look.</param>
    /// <param name="textureOverride">The texture's own override, or <see langword="null"/>.</param>
    /// <returns>The fully resolved look.</returns>
    public static LookSettings Resolve(ForgeRules rules, string? groupName, LookOverrides? textureOverride)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return ResolveScopes(rules.Look, FindGroup(rules, groupName)?.ScopeLook, textureOverride);
    }

    /// <summary>
    /// Resolves the three scopes control by control: <paramref name="texture"/>, else <paramref name="group"/>, else <paramref name="all"/>,
    /// else <see cref="LookSettings.Neutral"/>.
    /// </summary>
    /// <param name="all">The global look, or <see langword="null"/>.</param>
    /// <param name="group">The group look, or <see langword="null"/>.</param>
    /// <param name="texture">The texture override, or <see langword="null"/>.</param>
    /// <returns>The fully resolved look.</returns>
    public static LookSettings ResolveScopes(LookOverrides? all, LookOverrides? group, LookOverrides? texture)
    {
        LookOverrides combined = (texture ?? LookOverrides.Empty).Over(group).Over(all);
        return combined.Resolve(LookSettings.Neutral);
    }

    /// <summary>
    /// Finds a group by name, case-insensitively (the first match, as the evaluator would pick).
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="groupName">The group name, or <see langword="null"/>.</param>
    /// <returns>The group, or <see langword="null"/>.</returns>
    public static GroupRule? FindGroup(ForgeRules rules, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return groupName is null ? null : rules.Groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
    }
}
