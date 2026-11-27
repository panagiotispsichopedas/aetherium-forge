using AetheriumForge.Core.Jobs;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// Where a scope's look lives (§8 "Scopes"): the All and group looks in the rules, per-texture overrides in the project database.
/// The Look panel codes against this adapter; the Core look work (partial looks, <c>ForgeRules.Look</c>, the <c>texture_look</c> table,
/// the look resolver) plugs in behind it.
/// </summary>
public interface ILookScopeStore
{
    /// <summary>Reads the controls set at a scope.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="group">The group name (for <see cref="LookScope.Group"/>).</param>
    /// <param name="texturePath">The canonical texture path (for <see cref="LookScope.Texture"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The overrides; empty when nothing is set (or the group or texture is not given).</returns>
    Task<LookOverrides> GetAsync(LookScope scope, string? group, string? texturePath, CancellationToken cancellationToken);

    /// <summary>Stores the controls set at a scope (empty clears the scope).</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="group">The group name (required for <see cref="LookScope.Group"/>).</param>
    /// <param name="texturePath">The canonical texture path (required for <see cref="LookScope.Texture"/>).</param>
    /// <param name="look">The controls set at the scope.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task SaveAsync(LookScope scope, string? group, string? texturePath, LookOverrides look, CancellationToken cancellationToken);

    /// <summary>Lists the finished jobs a saved change at this scope affects (whose effective look may change).</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="group">The group name.</param>
    /// <param name="texturePath">The canonical texture path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The affected Done jobs.</returns>
    Task<IReadOnlyList<JobRecord>> FindAffectedFinishedAsync(LookScope scope, string? group, string? texturePath, CancellationToken cancellationToken);
}
