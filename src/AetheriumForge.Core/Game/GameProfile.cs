namespace AetheriumForge.Core.Game;

/// <summary>
/// What the game loads for one installation and user profile.
/// </summary>
/// <param name="DataFolder">The full path of the game's <c>Data</c> folder.</param>
/// <param name="LoadOrder">Plugins in load order, implicit masters and Creation Club plugins included.</param>
/// <param name="IniArchives">Archive file names from the INI archive lists, in order.</param>
/// <param name="PluginArchives">Existing plugin archive file names, in load order.</param>
public sealed record GameProfile(string DataFolder, IReadOnlyList<string> LoadOrder, IReadOnlyList<string> IniArchives, IReadOnlyList<string> PluginArchives);
