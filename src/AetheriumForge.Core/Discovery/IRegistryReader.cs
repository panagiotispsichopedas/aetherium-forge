using Microsoft.Win32;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Read-only access to the Windows registry, abstracted so discovery can be tested without a real registry.
/// </summary>
public interface IRegistryReader
{
    /// <summary>
    /// Reads a value as a string.
    /// </summary>
    /// <param name="hive">The root hive, for example <see cref="RegistryHive.CurrentUser"/>.</param>
    /// <param name="subKey">The sub key path below the hive, for example <c>Software\Valve\Steam</c>.</param>
    /// <param name="valueName">The value name, for example <c>SteamPath</c>.</param>
    /// <returns>The value as a string, or <see langword="null"/> when the key or value does not exist.</returns>
    string? GetString(RegistryHive hive, string subKey, string valueName);

    /// <summary>
    /// Lists the names of the direct sub keys of a key.
    /// </summary>
    /// <param name="hive">The root hive.</param>
    /// <param name="subKey">The sub key path below the hive.</param>
    /// <returns>The sub key names, or an empty list when the key does not exist.</returns>
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey);
}
