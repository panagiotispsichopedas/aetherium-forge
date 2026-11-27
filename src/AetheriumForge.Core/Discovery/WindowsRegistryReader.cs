using System.Globalization;
using Microsoft.Win32;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// <see cref="IRegistryReader"/> backed by the real Windows registry (64-bit view).
/// </summary>
/// <remarks>
/// Paths that need the 32-bit view name <c>WOW6432Node</c> explicitly, exactly as IMPLEMENTATION_GUIDE §4.1 lists them.
/// Access-denied errors propagate; callers decide whether one failing source is fatal.
/// </remarks>
public sealed class WindowsRegistryReader : IRegistryReader
{
    /// <inheritdoc />
    public string? GetString(RegistryHive hive, string subKey, string valueName)
    {
        ArgumentNullException.ThrowIfNull(subKey);
        ArgumentNullException.ThrowIfNull(valueName);

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey, writable: false);

        return key?.GetValue(valueName) switch
        {
            null => null,
            string text => text,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey)
    {
        ArgumentNullException.ThrowIfNull(subKey);

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey, writable: false);

        return key?.GetSubKeyNames() ?? [];
    }
}
