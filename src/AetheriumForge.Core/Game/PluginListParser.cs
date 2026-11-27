using System.Text.RegularExpressions;
using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Game;

/// <summary>
/// Parses plugin lists: <c>plugins.txt</c>, <c>Skyrim.ccc</c> and the <c>PLUGINS:</c> section of a CrashLogger SSE log
/// (IMPLEMENTATION_GUIDE §5, "Active plugins").
/// </summary>
/// <remarks>All methods are pure: they take file content and return data, with no file IO.</remarks>
public static partial class PluginListParser
{
    private const string CrashLoggerPluginsHeader = "PLUGINS:";

    /// <summary>
    /// Returns the active plugin names from <c>plugins.txt</c> content, in file order.
    /// </summary>
    /// <param name="pluginsTxtContent">The full content of <c>plugins.txt</c> (AE format).</param>
    /// <returns>
    /// Names of lines that start with <c>*</c> (the marker removed, whitespace trimmed). Lines starting with <c>#</c> are comments,
    /// blank lines are ignored and lines without <c>*</c> are inactive. A leading byte order mark is ignored.
    /// Duplicates are removed case-insensitively, keeping the first position.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="pluginsTxtContent"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseActive(string pluginsTxtContent)
    {
        ArgumentNullException.ThrowIfNull(pluginsTxtContent);

        var result = new List<string>();
        var seen = new HashSet<string>(GamePath.Comparer);
        foreach (string rawLine in SplitLines(pluginsTxtContent))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '*')
            {
                continue;
            }

            string name = line[1..].Trim();
            if (name.Length > 0 && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the plugin names listed in <c>Skyrim.ccc</c> content, in file order.
    /// </summary>
    /// <param name="cccContent">The full content of <c>Skyrim.ccc</c>: one plugin file name per line.</param>
    /// <returns>Trimmed, non-blank, non-comment (<c>#</c>) lines, deduplicated case-insensitively keeping the first position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cccContent"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseCcc(string cccContent)
    {
        ArgumentNullException.ThrowIfNull(cccContent);

        var result = new List<string>();
        var seen = new HashSet<string>(GamePath.Comparer);
        foreach (string rawLine in SplitLines(cccContent))
        {
            string line = rawLine.Trim();
            if (line.Length > 0 && line[0] != '#' && seen.Add(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the plugin names from the <c>PLUGINS:</c> section of a CrashLogger SSE log, in listed (load) order.
    /// Used as a fallback when <c>plugins.txt</c> is missing.
    /// </summary>
    /// <param name="logContent">The full content of a CrashLogger SSE crash log.</param>
    /// <returns>The plugin names in listed order, deduplicated case-insensitively; empty when the section is absent.</returns>
    /// <remarks>
    /// Assumed format (CrashLogger SSE AE VR, and the older CrashLogger by Ryan McKenzie):
    /// <code>
    /// PLUGINS:
    ///     Light: 2    Regular: 250    Total: 252
    ///     [00]     Skyrim.esm
    ///     [ 1] Update.esm
    ///     [FE:000] ccBGSSSE001-Fish.esm
    /// </code>
    /// The header must be exactly <c>PLUGINS:</c> on its own line (so <c>SKSE PLUGINS:</c> does not match). Inside the section, every line of
    /// the form <c>[index] Name.esp|esm|esl</c> yields a plugin; other lines (such as the light/regular counts) are skipped. The section ends at
    /// the next non-indented, non-blank line (the next section header) or at the end of the log.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="logContent"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseCrashLoggerPlugins(string logContent)
    {
        ArgumentNullException.ThrowIfNull(logContent);

        var result = new List<string>();
        var seen = new HashSet<string>(GamePath.Comparer);
        bool inSection = false;
        foreach (string rawLine in SplitLines(logContent))
        {
            string line = rawLine.TrimEnd('\r');
            if (!inSection)
            {
                inSection = string.Equals(line.Trim(), CrashLoggerPluginsHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            Match match = CrashLoggerPluginLine().Match(line);
            if (match.Success)
            {
                string name = match.Groups["name"].Value.Trim();
                if (seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private static string[] SplitLines(string content)
    {
        string text = content.Length > 0 && content[0] == '﻿' ? content[1..] : content;
        return text.Split('\n');
    }

    [GeneratedRegex(@"^\s*\[[^\]]*\]\s*(?<name>\S.*?\.(?:esp|esm|esl))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrashLoggerPluginLine();
}
