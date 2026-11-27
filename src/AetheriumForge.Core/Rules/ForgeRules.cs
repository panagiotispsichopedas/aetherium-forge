using System.Text.Json;
using AetheriumForge.Core.Common;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// The complete, user-editable rule set: groups, minimum source size and excluded folders (IMPLEMENTATION_GUIDE §8).
/// Persisted as camelCase JSON.
/// </summary>
/// <remarks>
/// Each group may carry a <c>look</c> block (§8, <see cref="LookSettings"/>); omitted look values keep their neutral defaults and
/// out-of-range values are rejected. The group-level <c>normalStrength</c> stays as a compatibility alias: a <c>look</c> without
/// <c>normalStrength</c> takes the group value, and when both are present <c>look.normalStrength</c> wins. Rules JSON written before
/// the look existed (no <c>look</c> block) reads unchanged.
/// </remarks>
public sealed record ForgeRules
{
    /// <summary>The default minimum source max dimension.</summary>
    public const int DefaultMinSource = 128;

    /// <summary>Gets the folders excluded by default (character actors, interface, LOD, terrain, effects, cube maps, DynDOLOD, tests).</summary>
    public static IReadOnlyList<string> DefaultExcludedFolders { get; } =
    [
        @"textures\actors\character\",
        @"textures\interface\",
        @"textures\lod\",
        @"textures\terrain\",
        @"textures\effects\",
        @"textures\cubemaps\",
        @"textures\dyndolod\",
        @"textures\test\",
        @"textures\shadertests\",
    ];

    /// <summary>
    /// Gets the default rules: a <c>Clutter</c> group (cap 2048, normal strength 1.4) followed by a catch-all <c>Large</c> group (cap 4096),
    /// both <c>realesrgan-x4plus</c> with max factor 4, minimum source 128 and the default excluded folders.
    /// </summary>
    public static ForgeRules Default { get; } = new()
    {
        Groups =
        [
            new GroupRule { Name = "Clutter", Match = [@"*\clutter\*"], Cap = 2048, MaxFactor = 4, Model = "realesrgan-x4plus", NormalStrength = 1.4 },
            new GroupRule { Name = "Large", Match = ["*"], Cap = 4096, MaxFactor = 4, Model = "realesrgan-x4plus", NormalStrength = 1.0 },
        ],
        MinSource = DefaultMinSource,
        Exclude = DefaultExcludedFolders,
    };

    /// <summary>Gets the groups, evaluated top-down; the first matching group wins.</summary>
    public IReadOnlyList<GroupRule> Groups { get; init; } = [];

    /// <summary>Gets the minimum source max dimension; smaller textures are skipped. Defaults to 128.</summary>
    public int MinSource { get; init; } = DefaultMinSource;

    /// <summary>
    /// Gets the excluded folder prefixes such as <c>textures\actors\character\</c>. Compared case-insensitively against canonical paths.
    /// Defaults to <see cref="DefaultExcludedFolders"/> when omitted from JSON.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExcludedFolders;

    /// <summary>
    /// Deserialises rules from JSON and validates them. Comments and trailing commas are allowed; omitted properties keep their defaults.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed or describes invalid rules.</exception>
    public static ForgeRules FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ForgeRulesDocument document = JsonSerializer.Deserialize(json, ForgeRulesJsonContext.Default.ForgeRulesDocument)
            ?? throw new JsonException("The rules JSON is null.");
        return document.ToRules();
    }

    /// <summary>
    /// Loads and validates rules from a JSON file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The file is malformed or describes invalid rules.</exception>
    public static async Task<ForgeRules> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileStream stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            ForgeRulesDocument document = await JsonSerializer.DeserializeAsync(stream, ForgeRulesJsonContext.Default.ForgeRulesDocument, ct).ConfigureAwait(false)
                ?? throw new JsonException($"The rules file '{path}' contains null.");
            return document.ToRules();
        }
    }

    /// <summary>
    /// Serialises the rules to indented camelCase JSON with enums as strings.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, ForgeRulesJsonContext.Default.ForgeRules);

    /// <summary>
    /// Saves the rules as JSON, creating the directory if needed. The file is written to a temporary file first and then moved into place.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{fullPath}.tmp";
        FileStream stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, ForgeRulesJsonContext.Default.ForgeRules, ct).ConfigureAwait(false);
        }

        File.Move(tempPath, fullPath, overwrite: true);
    }

    /// <summary>
    /// Returns whether <paramref name="canonicalPath"/> lies inside one of the <see cref="Exclude"/> folders.
    /// </summary>
    /// <param name="canonicalPath">A game path; it is compared case-insensitively and <c>/</c> is treated as <c>\</c>.</param>
    /// <returns><see langword="true"/> when the path is inside an excluded folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="canonicalPath"/> is <see langword="null"/>.</exception>
    public bool IsExcluded(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        return IsUnderAny(GamePath.Normalize(canonicalPath), NormalizeFolders(Exclude));
    }

    /// <summary>Normalises excluded folder prefixes, dropping empty entries.</summary>
    internal static string[] NormalizeFolders(IEnumerable<string> folders) =>
        [.. folders.Select(GamePath.Normalize).Where(f => f.Length > 0)];

    /// <summary>Returns whether the normalised <paramref name="path"/> lies strictly inside one of the normalised <paramref name="folders"/>.</summary>
    internal static bool IsUnderAny(string path, string[] folders)
    {
        foreach (string prefix in folders)
        {
            if (path.Length > prefix.Length && path[prefix.Length] == '\\' && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Determines whether two rule sets have equal values, comparing lists element by element.</summary>
    /// <param name="other">The rule set to compare with.</param>
    /// <returns><see langword="true"/> when all values are equal.</returns>
    public bool Equals(ForgeRules? other) =>
        other is not null
        && MinSource == other.MinSource
        && Groups.SequenceEqual(other.Groups)
        && Exclude.SequenceEqual(other.Exclude);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(MinSource, Groups.Count, Exclude.Count);

    /// <summary>
    /// Throws <see cref="JsonException"/> when the rules are not usable.
    /// </summary>
    internal void Validate()
    {
        if (Groups is null)
        {
            throw new JsonException("'groups' must be an array.");
        }

        if (Exclude is null || Exclude.Any(e => e is null))
        {
            throw new JsonException("'exclude' must be an array of folder paths.");
        }

        if (MinSource < 0)
        {
            throw new JsonException("'minSource' must not be negative.");
        }

        for (int i = 0; i < Groups.Count; i++)
        {
            GroupRule? group = Groups[i];
            if (group is null)
            {
                throw new JsonException($"Group #{i + 1} is null.");
            }

            string label = string.IsNullOrWhiteSpace(group.Name) ? $"Group #{i + 1}" : $"Group '{group.Name}'";
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                throw new JsonException($"{label}: 'name' is required.");
            }

            if (string.IsNullOrWhiteSpace(group.Model))
            {
                throw new JsonException($"{label}: 'model' is required.");
            }

            if (group.Match is null || group.Match.Count == 0 || group.Match.Any(string.IsNullOrWhiteSpace))
            {
                throw new JsonException($"{label}: 'match' must contain at least one non-empty pattern.");
            }

            if (group.Cap <= 0)
            {
                throw new JsonException($"{label}: 'cap' must be positive.");
            }

            if (group.MaxFactor < 1)
            {
                throw new JsonException($"{label}: 'maxFactor' must be at least 1.");
            }

            if (group.MinSourceMax is int min && group.MaxSourceMax is int max && min > max)
            {
                throw new JsonException($"{label}: 'minSourceMax' must not exceed 'maxSourceMax'.");
            }

            group.Look?.Validate(label);
        }
    }
}
