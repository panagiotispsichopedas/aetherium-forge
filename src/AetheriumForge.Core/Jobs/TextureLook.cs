using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Jobs;

/// <summary>
/// One row of the <c>texture_look</c> table: the "This texture" look override of one path (IMPLEMENTATION_GUIDE §8, "Scopes").
/// </summary>
/// <param name="Path">The canonical game path (normalised, lowercase).</param>
/// <param name="Look">The controls this texture sets.</param>
/// <param name="UpdatedAt">When the override was last written.</param>
public sealed record TextureLook(string Path, LookOverrides Look, DateTimeOffset UpdatedAt);
