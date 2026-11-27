using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Game;

/// <summary>
/// Builds the effective plugin load order (IMPLEMENTATION_GUIDE §5, "Active plugins").
/// </summary>
public static class LoadOrderBuilder
{
    /// <summary>
    /// The plugins the game always loads first, in order, even when <c>plugins.txt</c> does not list them.
    /// </summary>
    public static IReadOnlyList<string> ImplicitMasters { get; } =
        ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>
    /// Builds the load order: <see cref="ImplicitMasters"/>, then the <c>Skyrim.ccc</c> entries that exist in Data, then the active plugins.
    /// </summary>
    /// <param name="activePlugins">Active plugins in <c>plugins.txt</c> order.</param>
    /// <param name="cccEntries">Creation Club plugin names from <c>Skyrim.ccc</c>, in file order.</param>
    /// <param name="existsInData">Returns <see langword="true"/> when a plugin file name exists in the Data folder.</param>
    /// <returns>The load order, deduplicated case-insensitively keeping the first position.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Build(IEnumerable<string> activePlugins, IEnumerable<string> cccEntries, Func<string, bool> existsInData)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(cccEntries);
        ArgumentNullException.ThrowIfNull(existsInData);

        var result = new List<string>();
        var seen = new HashSet<string>(GamePath.Comparer);

        foreach (string master in ImplicitMasters)
        {
            Add(master);
        }

        foreach (string entry in cccEntries)
        {
            string name = entry.Trim();
            if (name.Length > 0 && !seen.Contains(name) && existsInData(name))
            {
                Add(name);
            }
        }

        foreach (string plugin in activePlugins)
        {
            string name = plugin.Trim();
            if (name.Length > 0)
            {
                Add(name);
            }
        }

        return result;

        void Add(string name)
        {
            if (seen.Add(name))
            {
                result.Add(name);
            }
        }
    }
}
