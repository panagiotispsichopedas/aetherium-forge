namespace AetheriumForge.Core.Projects;

/// <summary>
/// Path containment checks: Aetherium Forge never writes inside the game folder.
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Tells whether <paramref name="path"/> is <paramref name="folder"/> itself or anything below it. Works for a folder at a drive root
    /// (<c>D:\</c>) and ignores case and trailing separators.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <param name="folder">The containing folder.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is inside <paramref name="folder"/>.</returns>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var prefix = Path.GetFullPath(folder);
        if (!Path.EndsInDirectorySeparator(prefix))
        {
            prefix += Path.DirectorySeparatorChar;
        }

        var full = Path.GetFullPath(path);
        if (!Path.EndsInDirectorySeparator(full))
        {
            full += Path.DirectorySeparatorChar;
        }

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
