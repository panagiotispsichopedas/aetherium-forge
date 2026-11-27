namespace AetheriumForge.Core.Projects;

/// <summary>
/// Path containment checks: Aetherium Forge never writes inside the game folder.
/// </summary>
public static class PathGuard
{
    /// <summary>How many links are followed before a path is taken as it is (guards against link cycles).</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Tells whether <paramref name="path"/> is <paramref name="folder"/> itself or anything below it. Works for a folder at a drive root
    /// (<c>D:\</c>) and ignores case and trailing separators. Symbolic links and junctions in either path are resolved too, so a project
    /// reached through a link into the game folder is still inside it.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <param name="folder">The containing folder.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is inside <paramref name="folder"/>.</returns>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return IsUnderLexically(path, folder) || IsUnderLexically(ResolveFinalPath(path), ResolveFinalPath(folder));
    }

    /// <summary>
    /// Resolves symbolic links and junctions along <paramref name="path"/>: every existing segment that is a link is replaced by its final
    /// target; segments that do not exist yet are kept as they are.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The full, resolved path.</returns>
    public static string ResolveFinalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var current = Path.GetFullPath(path);
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            string? resolved;
            try
            {
                resolved = ResolveFirstLink(current);
            }
            catch (IOException)
            {
                // A broken or unreachable link: keep the path as it is; IsUnder still compares it as text.
                return current;
            }
            catch (UnauthorizedAccessException)
            {
                return current;
            }

            if (resolved is null)
            {
                return current;
            }

            current = resolved;
        }

        return current;
    }

    /// <summary>Tells whether <paramref name="path"/> is under <paramref name="folder"/> by comparing the full paths as text.</summary>
    private static bool IsUnderLexically(string path, string folder)
    {
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

    /// <summary>Replaces the first existing link segment of <paramref name="fullPath"/> by its target, or returns <see langword="null"/>.</summary>
    private static string? ResolveFirstLink(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var prefix = root;
        for (var i = 0; i < segments.Length; i++)
        {
            prefix = Path.Combine(prefix, segments[i]);
            FileSystemInfo info = Directory.Exists(prefix) ? new DirectoryInfo(prefix) : new FileInfo(prefix);
            if (!info.Exists)
            {
                return null;
            }

            if (info.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return null;
            }

            return Path.Combine([target.FullName, .. segments[(i + 1)..]]);
        }

        return null;
    }
}
