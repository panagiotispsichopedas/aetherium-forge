using AetheriumForge.Core.Discovery;

namespace AetheriumForge.Core.Projects;

/// <summary>
/// Chooses the project folder. The default is <c>&lt;best drive&gt;\AetheriumForge\Projects\&lt;game folder name&gt;</c>, where the best drive
/// comes from <see cref="DriveAdvisor.Rank"/>; an existing project for the same game on any drive is reused, so later commands find it again.
/// </summary>
public static class ProjectLocator
{
    /// <summary>The folder under a drive root that holds the projects.</summary>
    public static readonly string ProjectsFolder = Path.Combine("AetheriumForge", "Projects");

    /// <summary>
    /// Gets the default project folder for a game on a drive.
    /// </summary>
    /// <param name="driveRoot">For example <c>D:\</c>.</param>
    /// <param name="gameFolder">The game folder; its last segment names the project.</param>
    /// <returns>The project folder.</returns>
    public static string DefaultProjectFolder(string driveRoot, string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        return Path.Combine(driveRoot, ProjectsFolder, GameFolderName(gameFolder));
    }

    /// <summary>Gets the last segment of the game folder, for example <c>Skyrim Special Edition</c>.</summary>
    /// <param name="gameFolder">The game folder.</param>
    /// <returns>The folder name.</returns>
    public static string GameFolderName(string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(gameFolder));
        return string.IsNullOrWhiteSpace(name) ? "Game" : name;
    }

    /// <summary>
    /// Resolves the project folder for <c>scan</c>.
    /// </summary>
    /// <param name="explicitFolder">The <c>--project</c> value, or <see langword="null"/>.</param>
    /// <param name="gameFolder">The game folder.</param>
    /// <param name="drives">Fixed drives (root, free bytes, is system drive).</param>
    /// <param name="requiredBytes">Estimated space the project needs.</param>
    /// <param name="fileExists">File existence probe (injected for tests).</param>
    /// <returns>The layout and a short reason shown to the user.</returns>
    /// <exception cref="ProjectException">No drive is available, or the folder is inside the game folder.</exception>
    public static (ProjectLayout Layout, string Reason) ResolveForScan(
        string? explicitFolder,
        string gameFolder,
        IReadOnlyList<(string Root, long Free, bool IsSystem)> drives,
        long requiredBytes,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(fileExists);

        (ProjectLayout, string) result;
        if (!string.IsNullOrWhiteSpace(explicitFolder))
        {
            result = (ProjectLayout.For(explicitFolder), "from --project");
        }
        else if (FindExisting(gameFolder, drives, fileExists) is { } existing)
        {
            result = (existing, "existing project");
        }
        else
        {
            // A game installed at a drive root (D:\) owns that whole drive: never default the project onto it.
            var ranked = DriveAdvisor.Rank(drives, requiredBytes)
                .Where(d => !PathGuard.IsUnder(DefaultProjectFolder(d.RootPath, gameFolder), gameFolder))
                .ToList();
            if (ranked.Count == 0)
            {
                throw new ProjectException("No fixed drive outside the game folder is available for the project folder. Pass --project <dir>.");
            }

            var best = ranked[0];
            var reason = best.Fits
                ? $"best drive: {FormatBytes(best.FreeBytes)} free{(best.IsSystemDrive ? ", system drive" : string.Empty)}"
                : $"WARNING: no drive has the estimated {FormatBytes(requiredBytes)} free; using the roomiest";
            result = (ProjectLayout.For(DefaultProjectFolder(best.RootPath, gameFolder)), reason);
        }

        EnsureOutsideGame(result.Item1, gameFolder);
        return result;
    }

    /// <summary>
    /// Resolves an existing project for <c>forge</c> and <c>status</c>.
    /// </summary>
    /// <param name="explicitFolder">The <c>--project</c> value, or <see langword="null"/>.</param>
    /// <param name="gameFolders">Discovered game folders (may be empty).</param>
    /// <param name="drives">Fixed drives.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The layout of a folder that holds <c>forge.db</c>.</returns>
    /// <exception cref="ProjectException">No project was found.</exception>
    public static ProjectLayout ResolveExisting(
        string? explicitFolder,
        IReadOnlyList<string> gameFolders,
        IReadOnlyList<(string Root, long Free, bool IsSystem)> drives,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(gameFolders);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(explicitFolder))
        {
            var layout = ProjectLayout.For(explicitFolder);
            if (!fileExists(layout.DatabasePath))
            {
                throw new ProjectException($"{layout.Root} is not a project (no {ProjectLayout.DatabaseFileName}). Run 'aetherium-forge scan --project \"{layout.Root}\"' first.");
            }

            return layout;
        }

        foreach (var gameFolder in gameFolders)
        {
            if (FindExisting(gameFolder, drives, fileExists) is { } existing)
            {
                return existing;
            }
        }

        throw new ProjectException("No project found. Run 'aetherium-forge scan' first, or pass --project <dir>.");
    }

    /// <summary>Finds a project for the game on any of the drives.</summary>
    /// <param name="gameFolder">The game folder.</param>
    /// <param name="drives">Fixed drives, in any order.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The first project found, or <see langword="null"/>.</returns>
    public static ProjectLayout? FindExisting(string gameFolder, IReadOnlyList<(string Root, long Free, bool IsSystem)> drives, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (var drive in drives.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase))
        {
            var layout = ProjectLayout.For(DefaultProjectFolder(drive.Root, gameFolder));
            if (fileExists(layout.DatabasePath))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>Rejects a project folder inside the game folder: Aetherium Forge never writes there.</summary>
    /// <param name="layout">The project.</param>
    /// <param name="gameFolder">The game folder.</param>
    /// <exception cref="ProjectException">The project is inside the game folder.</exception>
    public static void EnsureOutsideGame(ProjectLayout layout, string gameFolder)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);

        if (PathGuard.IsUnder(layout.Root, gameFolder))
        {
            throw new ProjectException($"The project folder {layout.Root} is inside the game folder {gameFolder}. Choose a folder outside the game with --project.");
        }
    }

    /// <summary>Formats a byte count as KB/MB/GB/TB.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>For example <c>12.3 GB</c>.</returns>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}
