using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Game;

/// <summary>
/// Resolves the INI archive list (<c>[Archive] sResourceArchiveList</c> and <c>sResourceArchiveList2</c>) across the
/// Skyrim INI override chain (IMPLEMENTATION_GUIDE §5, item 3).
/// </summary>
public static class IniArchiveList
{
    /// <summary>The INI section holding the archive lists.</summary>
    public const string ArchiveSection = "Archive";

    /// <summary>The primary archive list key.</summary>
    public const string ListKey = "sResourceArchiveList";

    /// <summary>The secondary archive list key.</summary>
    public const string List2Key = "sResourceArchiveList2";

    /// <summary>
    /// Resolves the archive file names from INI contents in override order.
    /// </summary>
    /// <param name="iniContentsInOverrideOrder">
    /// INI file contents, earliest first (<c>Skyrim_Default.ini</c>, <c>Skyrim.ini</c>, <c>SkyrimCustom.ini</c>). A later file overrides an
    /// earlier one per key (an explicitly empty value overrides too); a key the later file does not set keeps the earlier value.
    /// </param>
    /// <returns>
    /// The archive names from <see cref="ListKey"/> then <see cref="List2Key"/>, comma-separated and trimmed, blanks removed,
    /// deduplicated case-insensitively keeping the first position.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="iniContentsInOverrideOrder"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> iniContentsInOverrideOrder)
    {
        ArgumentNullException.ThrowIfNull(iniContentsInOverrideOrder);

        string? list = null;
        string? list2 = null;
        foreach (string content in iniContentsInOverrideOrder)
        {
            if (content is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, string> values = ReadSection(content, ArchiveSection);
            if (values.TryGetValue(ListKey, out string? listValue))
            {
                list = listValue;
            }

            if (values.TryGetValue(List2Key, out string? list2Value))
            {
                list2 = list2Value;
            }
        }

        var result = new List<string>();
        var seen = new HashSet<string>(GamePath.Comparer);
        foreach (string? value in new[] { list, list2 })
        {
            if (value is null)
            {
                continue;
            }

            foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(part))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the key/value pairs of one section of INI content. Section and key names are case-insensitive; lines starting with
    /// <c>;</c> or <c>#</c> are comments; when a key repeats (including in a repeated section), the last value wins.
    /// </summary>
    /// <param name="iniContent">The INI file content.</param>
    /// <param name="sectionName">The section name without brackets.</param>
    /// <returns>The section's values keyed case-insensitively; empty when the section is absent.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, string> ReadSection(string iniContent, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(iniContent);
        ArgumentNullException.ThrowIfNull(sectionName);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = iniContent.Length > 0 && iniContent[0] == '﻿' ? iniContent[1..] : iniContent;
        bool inSection = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']', StringComparison.Ordinal);
                string name = close > 0 ? line[1..close].Trim() : line[1..].Trim();
                inSection = string.Equals(name, sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            if (key.Length > 0)
            {
                values[key] = line[(equals + 1)..].Trim();
            }
        }

        return values;
    }
}
