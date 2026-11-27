namespace AetheriumForge.Core.Projects;

/// <summary>
/// Keys of the project key/value table written by <c>scan</c> and read by <c>forge</c> and <c>status</c>.
/// </summary>
public static class ProjectKeys
{
    /// <summary>The game folder (the one that holds <c>SkyrimSE.exe</c>).</summary>
    public const string GamePath = "gamePath";

    /// <summary>The store the game was installed from (<c>Steam</c>, <c>Gog</c>, ...).</summary>
    public const string GameStore = "gameStore";

    /// <summary>The rules used by the last scan, as JSON.</summary>
    public const string RulesJson = "rulesJson";

    /// <summary>When the last scan finished (ISO 8601, UTC).</summary>
    public const string ScannedAt = "scannedAt";
}
