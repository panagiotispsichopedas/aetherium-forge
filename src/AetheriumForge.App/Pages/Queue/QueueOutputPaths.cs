using AetheriumForge.Core.Common;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// Maps a job's game path to its output file for Re-queue, which deletes that file. Only a path that resolves to a file inside
/// <c>&lt;OutputRoot&gt;\textures</c> is accepted, so a malformed row can never delete anything else.
/// </summary>
public static class QueueOutputPaths
{
    /// <summary>The output subfolder that holds textures.</summary>
    public const string TexturesFolder = "textures";

    /// <summary>
    /// Resolves the output file of <paramref name="jobPath"/>.
    /// </summary>
    /// <param name="outputRoot">The project's output root.</param>
    /// <param name="jobPath">The job's game path, for example <c>textures\clutter\bucket01.dds</c> (normalised with <see cref="GamePath"/>).</param>
    /// <param name="outputFile">The full output file path when accepted.</param>
    /// <returns><see langword="true"/> when the path is relative, has no <c>..</c> segment and resolves to a file inside
    /// <c>&lt;OutputRoot&gt;\textures</c>.</returns>
    public static bool TryResolve(string outputRoot, string jobPath, out string outputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(jobPath);

        outputFile = string.Empty;
        var canonical = GamePath.Normalize(jobPath);
        if (canonical.Length == 0 || Path.IsPathRooted(canonical) || canonical.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = canonical.Split('\\');
        if (segments.Any(segment => segment is ".." or "." or ""))
        {
            return false;
        }

        string texturesRoot;
        string fullPath;
        try
        {
            texturesRoot = Path.GetFullPath(Path.Combine(outputRoot, TexturesFolder));
            fullPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(outputRoot), canonical));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the OS cannot even represent is never a file we would delete.
            return false;
        }

        var texturesRootWithSeparator = Path.EndsInDirectorySeparator(texturesRoot) ? texturesRoot : texturesRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(texturesRootWithSeparator, StringComparison.OrdinalIgnoreCase) || fullPath.Length == texturesRootWithSeparator.Length)
        {
            return false;
        }

        outputFile = fullPath;

        return true;
    }

    /// <summary>
    /// Checks that nothing between <c>&lt;OutputRoot&gt;	extures</c> and <paramref name="outputFile"/> is a reparse point
    /// (junction or symbolic link), so deleting the file cannot reach outside the output folder through a link. Call it right
    /// before deleting a file that <see cref="TryResolve"/> accepted.
    /// </summary>
    /// <param name="outputRoot">The project's output root.</param>
    /// <param name="outputFile">A file accepted by <see cref="TryResolve"/>.</param>
    /// <returns><see langword="true"/> when the textures folder, every folder below it on the way to the file and the file itself
    /// (those that exist) are ordinary entries; <see langword="false"/> when any is a reparse point or the file is not under the
    /// textures folder.</returns>
    public static bool IsFreeOfLinks(string outputRoot, string outputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        var texturesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(outputRoot, TexturesFolder)));
        var current = Path.GetFullPath(outputFile);
        if (!current.StartsWith(texturesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        while (current.Length >= texturesRoot.Length)
        {
            // FileSystemInfo.Attributes reads the entry itself (a link is not followed) and is -1 when the entry is missing.
            var attributes = new FileInfo(current).Attributes;
            if ((int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (current.Length == texturesRoot.Length)
            {
                break;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return true;
    }
}
