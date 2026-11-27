namespace AetheriumForge.Core.Common;

/// <summary>
/// Normalises game resource paths to the canonical form used everywhere in Aetherium Forge:
/// lowercase, backslash separators, no leading or trailing separators.
/// </summary>
/// <remarks>
/// The game resolves resources case-insensitively with backslash separators (IMPLEMENTATION_GUIDE §5),
/// so every path that is compared, hashed or stored must go through <see cref="Normalize"/>.
/// </remarks>
public static class GamePath
{
    /// <summary>
    /// Returns the canonical form of <paramref name="path"/>.
    /// </summary>
    /// <param name="path">A resource path such as <c>Textures/Clutter/Bucket01.DDS</c>.</param>
    /// <returns>The canonical path, for example <c>textures\clutter\bucket01.dds</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Replace('/', '\\').Trim().Trim('\\').ToLowerInvariant();
    }

    /// <summary>
    /// Case-insensitive comparer for canonical or non-canonical game paths.
    /// </summary>
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;
}
