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

        var texturesRoot = Path.GetFullPath(Path.Combine(outputRoot, TexturesFolder));
        var texturesRootWithSeparator = Path.EndsInDirectorySeparator(texturesRoot) ? texturesRoot : texturesRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(outputRoot), canonical));
        if (!fullPath.StartsWith(texturesRootWithSeparator, StringComparison.OrdinalIgnoreCase) || fullPath.Length == texturesRootWithSeparator.Length)
        {
            return false;
        }

        outputFile = fullPath;

        return true;
    }
}
