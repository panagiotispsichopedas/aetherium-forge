namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// The files and folders of one project (IMPLEMENTATION_GUIDE §12): <c>forge.db</c>, <c>work\</c>, <c>output\</c> and <c>logs\</c>.
/// </summary>
/// <param name="Root">The project folder (full path).</param>
internal sealed record ProjectLayout(string Root)
{
    /// <summary>The database file name inside the project folder.</summary>
    public const string DatabaseFileName = "forge.db";

    /// <summary>Gets the SQLite job store.</summary>
    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>Gets the folder for temporary files; wiped when a run starts.</summary>
    public string WorkRoot => Path.Combine(Root, "work");

    /// <summary>Gets the mod output folder; textures go to <c>output\textures\...</c>.</summary>
    public string OutputRoot => Path.Combine(Root, "output");

    /// <summary>Gets the log folder.</summary>
    public string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>Gets the default log file for <paramref name="date"/>.</summary>
    /// <param name="date">The local date.</param>
    /// <returns><c>logs\aetherium-forge-yyyyMMdd.log</c>.</returns>
    public string DefaultLogFile(DateOnly date) => Path.Combine(LogsFolder, $"aetherium-forge-{date:yyyyMMdd}.log");

    /// <summary>Creates a layout for a folder, normalising it to a full path.</summary>
    /// <param name="folder">The project folder.</param>
    /// <returns>The layout.</returns>
    public static ProjectLayout For(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return new ProjectLayout(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
    }
}
