namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Finds <c>texconv.exe</c>: <c>--texconv</c>, then <c>&lt;exe dir&gt;\tools\texconv.exe</c>, then <c>&lt;repo&gt;\tools\texconv.exe</c>, where the repo is
/// the nearest parent folder that holds <c>AetheriumForge.slnx</c>.
/// </summary>
internal static class ToolLocator
{
    /// <summary>The texconv executable name.</summary>
    public const string TexconvFileName = "texconv.exe";

    /// <summary>The solution file that marks the repository root.</summary>
    public const string SolutionFileName = "AetheriumForge.slnx";

    /// <summary>
    /// Lists the texconv candidates in lookup order.
    /// </summary>
    /// <param name="explicitPath">The <c>--texconv</c> value, or <see langword="null"/>.</param>
    /// <param name="baseDirectory">The executable's folder (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="fileExists">File existence probe (injected for tests).</param>
    /// <returns>The candidates; an explicit path is the only candidate.</returns>
    public static IReadOnlyList<string> TexconvCandidates(string? explicitPath, string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return [Path.GetFullPath(explicitPath)];
        }

        var candidates = new List<string> { Path.Combine(baseDirectory, "tools", TexconvFileName) };
        if (FindRepositoryRoot(baseDirectory, fileExists) is { } repo)
        {
            var repoTool = Path.Combine(repo, "tools", TexconvFileName);
            if (!candidates.Contains(repoTool, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(repoTool);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Finds texconv.
    /// </summary>
    /// <param name="explicitPath">The <c>--texconv</c> value, or <see langword="null"/>.</param>
    /// <param name="baseDirectory">The executable's folder.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The full path of texconv.exe.</returns>
    /// <exception cref="CliException">texconv was not found; the message lists the paths tried and how to get it.</exception>
    public static string FindTexconv(string? explicitPath, string baseDirectory, Func<string, bool> fileExists)
    {
        var candidates = TexconvCandidates(explicitPath, baseDirectory, fileExists);
        foreach (var candidate in candidates)
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        var tried = string.Join(Environment.NewLine, candidates.Select(c => $"  - {c}"));
        var hint = string.IsNullOrWhiteSpace(explicitPath)
            ? $"Download it with 'pwsh tools\\fetch-tools.ps1' (from the repository root), or pass --texconv <path>."
            : "Check the --texconv path, or omit it to use tools\\texconv.exe (get it with 'pwsh tools\\fetch-tools.ps1').";

        throw new CliException($"texconv.exe was not found. Tried:{Environment.NewLine}{tried}{Environment.NewLine}{hint}");
    }

    /// <summary>Walks up from <paramref name="start"/> to the folder that holds <see cref="SolutionFileName"/>.</summary>
    /// <param name="start">The first folder to look in.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The repository root, or <see langword="null"/>.</returns>
    public static string? FindRepositoryRoot(string start, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        for (var dir = Path.GetFullPath(start); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (fileExists(Path.Combine(dir, SolutionFileName)))
            {
                return Path.TrimEndingDirectorySeparator(dir);
            }
        }

        return null;
    }
}
