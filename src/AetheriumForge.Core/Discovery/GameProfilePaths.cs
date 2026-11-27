namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Where the game keeps per-user profile files (IMPLEMENTATION_GUIDE §4.2).
/// </summary>
/// <param name="IniFolder">Folder holding <c>Skyrim.ini</c>, <c>SkyrimPrefs.ini</c> and <c>SkyrimCustom.ini</c>.</param>
/// <param name="PluginsTxtPath">Full path of <c>plugins.txt</c> (active plugins and load order).</param>
public sealed record GameProfilePaths(string IniFolder, string PluginsTxtPath);
