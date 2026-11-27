using AetheriumForge.Core.Common;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// <see cref="ILookScopeStore"/> over Core's look scopes (§8): All = <see cref="ForgeRules.Look"/>, Group = <see cref="GroupRule.Look"/>
/// (read through <see cref="GroupRule.ScopeLook"/>, so the normal-strength alias counts), This texture = the <c>texture_look</c> table.
/// Affected finished textures come from <see cref="JobStore.FindDoneAffectedByLookChangeAsync"/> with <see cref="LookResolver"/>.
/// </summary>
public sealed class CoreLookScopeStore : ILookScopeStore
{
    private readonly ICompareProjectData _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoreLookScopeStore"/> class.
    /// </summary>
    /// <param name="data">The project rules and jobs.</param>
    public CoreLookScopeStore(ICompareProjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <inheritdoc />
    public async Task<LookOverrides> GetAsync(LookScope scope, string? group, string? texturePath, CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case LookScope.All:
                return (await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false)).Look ?? LookOverrides.Empty;
            case LookScope.Group:
                if (group is null)
                {
                    return LookOverrides.Empty;
                }

                var rules = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
                return LookResolver.FindGroup(rules, group)?.ScopeLook ?? LookOverrides.Empty;
            default:
                return texturePath is null
                    ? LookOverrides.Empty
                    : await _data.GetTextureLookAsync(texturePath, cancellationToken).ConfigureAwait(false) ?? LookOverrides.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(LookScope scope, string? group, string? texturePath, LookOverrides look, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(look);
        switch (scope)
        {
            case LookScope.All:
                var rules = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
                await _data.SaveRulesAsync(rules with { Look = look.NullIfEmpty() }, cancellationToken).ConfigureAwait(false);
                break;
            case LookScope.Group:
                ArgumentNullException.ThrowIfNull(group);
                var current = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
                var groups = current.Groups.ToList();
                var index = groups.FindIndex(rule => string.Equals(rule.Name, group, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new InvalidOperationException($"Group {group} is no longer in the rules.");
                }

                // The look carries the normal strength; the group-level alias follows it so ScopeLook reads back what was saved.
                groups[index] = groups[index] with { Look = look.NullIfEmpty(), NormalStrength = look.NormalStrength ?? 1.0 };
                await _data.SaveRulesAsync(current with { Groups = groups }, cancellationToken).ConfigureAwait(false);
                break;
            default:
                ArgumentNullException.ThrowIfNull(texturePath);
                await _data.SetTextureLookAsync(texturePath, look, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    /// <remarks>Call after <see cref="SaveAsync"/>: each Done job's effective look is resolved from the saved rules and texture overrides.</remarks>
    public async Task<IReadOnlyList<JobRecord>> FindAffectedFinishedAsync(LookScope scope, string? group, string? texturePath, CancellationToken cancellationToken)
    {
        if (scope == LookScope.Texture && texturePath is null)
        {
            return [];
        }

        var rules = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
        var affected = await _data.FindDoneAffectedByLookChangeAsync(
            (job, texture) => LookResolver.Resolve(rules, job.Group, texture),
            scope == LookScope.Group ? group : null,
            cancellationToken).ConfigureAwait(false);

        return scope == LookScope.Texture ? [.. affected.Where(job => GamePath.Comparer.Equals(job.Path, texturePath))] : affected;
    }
}
