namespace AetheriumForge.Core.Game;

/// <summary>
/// Lists the archives the game loads for active plugins (IMPLEMENTATION_GUIDE §5, item 2).
/// </summary>
public static class PluginArchives
{
    private static readonly string[] PluginExtensions = [".esp", ".esm", ".esl"];

    /// <summary>
    /// Returns, for each plugin <c>X.esp/.esm/.esl</c> in load order, <c>X.bsa</c> then <c>X - Textures.bsa</c>, keeping only archives that exist.
    /// </summary>
    /// <param name="loadOrder">The plugin load order.</param>
    /// <param name="existsInData">Returns <see langword="true"/> when an archive file name exists in the Data folder.</param>
    /// <returns>
    /// Existing plugin archives in load order (later wins), deduplicated case-insensitively. Entries without a plugin extension are skipped.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> For(IReadOnlyList<string> loadOrder, Func<string, bool> existsInData)
    {
        ArgumentNullException.ThrowIfNull(loadOrder);
        ArgumentNullException.ThrowIfNull(existsInData);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string plugin in loadOrder)
        {
            string name = plugin.Trim();
            string extension = Path.GetExtension(name);
            if (!PluginExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string baseName = name[..^extension.Length];
            foreach (string archive in new[] { $"{baseName}.bsa", $"{baseName} - Textures.bsa" })
            {
                if (!seen.Contains(archive) && existsInData(archive))
                {
                    seen.Add(archive);
                    result.Add(archive);
                }
            }
        }

        return result;
    }
}
