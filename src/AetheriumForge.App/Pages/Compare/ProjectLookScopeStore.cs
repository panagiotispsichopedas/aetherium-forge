using System.Text.Json;
using AetheriumForge.Core.Common;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// Interim <see cref="ILookScopeStore"/> until the Core look-scope work (PR #19 follow-up) lands.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Group:</b> the group's <see cref="GroupRule.Look"/> in the rules (the pipeline already applies it). Core's look is a full
/// <see cref="LookSettings"/>, so reading treats non-neutral controls as set, and saving fills the unset controls with neutral. The
/// group-level <see cref="GroupRule.NormalStrength"/> alias is kept in sync.</item>
/// <item><b>All</b> and <b>This texture:</b> stored in the project key/value table (<see cref="AllLookKey"/>,
/// <see cref="TextureLooksKey"/>) because <c>ForgeRules.Look</c> and the <c>texture_look</c> table do not exist yet. The pipeline does
/// not read them yet.</item>
/// <item><b>Affected jobs:</b> every Done job in the scope; there is no effective-look hash to compare yet.</item>
/// </list>
/// TODO(M5 look scopes): replace with the Core adapter (ForgeRules.Look, texture_look, LookResolver hashes) after PR #19 merges.
/// </remarks>
public sealed class ProjectLookScopeStore : ILookScopeStore
{
    /// <summary>The interim project key of the All look.</summary>
    public const string AllLookKey = "compare.allLook";

    /// <summary>The interim project key of the per-texture overrides (a JSON object: path → control values).</summary>
    public const string TextureLooksKey = "compare.textureLooks";

    private readonly ICompareProjectData _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectLookScopeStore"/> class.
    /// </summary>
    /// <param name="data">The project rules, jobs and key/value settings.</param>
    public ProjectLookScopeStore(ICompareProjectData data)
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
                return LookOverrides.FromNamedValues(Parse<Dictionary<string, double>>(await _data.GetProjectValueAsync(AllLookKey, cancellationToken).ConfigureAwait(false)));
            case LookScope.Group:
                if (group is null)
                {
                    return LookOverrides.Empty;
                }

                var rules = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
                return rules.Groups.FirstOrDefault(rule => string.Equals(rule.Name, group, StringComparison.OrdinalIgnoreCase)) is { } match ? FromGroup(match) : LookOverrides.Empty;
            default:
                if (texturePath is null)
                {
                    return LookOverrides.Empty;
                }

                var textures = await ReadTexturesAsync(cancellationToken).ConfigureAwait(false);
                return textures.TryGetValue(GamePath.Normalize(texturePath), out var values) ? LookOverrides.FromNamedValues(values) : LookOverrides.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(LookScope scope, string? group, string? texturePath, LookOverrides look, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(look);
        switch (scope)
        {
            case LookScope.All:
                await _data.SetProjectValueAsync(AllLookKey, JsonSerializer.Serialize(look.ToNamedValues()), cancellationToken).ConfigureAwait(false);
                break;
            case LookScope.Group:
                ArgumentNullException.ThrowIfNull(group);
                var rules = await _data.LoadRulesAsync(cancellationToken).ConfigureAwait(false);
                var groups = rules.Groups.ToList();
                var index = groups.FindIndex(rule => string.Equals(rule.Name, group, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    throw new InvalidOperationException($"Group {group} is no longer in the rules.");
                }

                var full = look.ApplyTo(LookSettings.Neutral);
                groups[index] = groups[index] with { Look = look.IsEmpty ? null : full, NormalStrength = full.NormalStrength };
                await _data.SaveRulesAsync(rules with { Groups = groups }, cancellationToken).ConfigureAwait(false);
                break;
            default:
                ArgumentNullException.ThrowIfNull(texturePath);
                var textures = await ReadTexturesAsync(cancellationToken).ConfigureAwait(false);
                var key = GamePath.Normalize(texturePath);
                if (look.IsEmpty)
                {
                    textures.Remove(key);
                }
                else
                {
                    textures[key] = look.ToNamedValues();
                }

                await _data.SetProjectValueAsync(TextureLooksKey, JsonSerializer.Serialize(textures), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> FindAffectedFinishedAsync(LookScope scope, string? group, string? texturePath, CancellationToken cancellationToken)
    {
        var finished = await _data.GetFinishedJobsAsync(cancellationToken).ConfigureAwait(false);
        return scope switch
        {
            LookScope.All => finished,
            LookScope.Group => [.. finished.Where(job => string.Equals(job.Group, group, StringComparison.OrdinalIgnoreCase))],
            _ => texturePath is null ? [] : [.. finished.Where(job => GamePath.Comparer.Equals(job.Path, texturePath))],
        };
    }

    /// <summary>Reads a group's interim overrides: its look's non-neutral controls, or the normal-strength alias when it has no look.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The overrides.</returns>
    internal static LookOverrides FromGroup(GroupRule group)
    {
        if (group.Look is { } look)
        {
            return LookOverrides.FromLook(look, onlyNonNeutral: true);
        }

        return group.NormalStrength.Equals(1.0) ? LookOverrides.Empty : LookOverrides.Empty.With(LookControl.NormalStrength, group.NormalStrength);
    }

    private static T? Parse<T>(string? json)
        where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);

    private async Task<Dictionary<string, Dictionary<string, double>>> ReadTexturesAsync(CancellationToken cancellationToken) =>
        Parse<Dictionary<string, Dictionary<string, double>>>(await _data.GetProjectValueAsync(TextureLooksKey, cancellationToken).ConfigureAwait(false))
        ?? new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
}
