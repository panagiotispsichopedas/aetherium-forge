using System.Text.Json;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Jobs;

/// <summary>
/// Exports and imports a project's per-texture look overrides as <c>texture-looks.json</c> (IMPLEMENTATION_GUIDE §8): the rules stay
/// portable, and the overrides travel separately.
/// </summary>
/// <remarks>
/// Format: <c>{ "version": 1, "textures": [ { "path": "textures\\...\\x.dds", "look": { "saturation": 1.2 } } ] }</c>, with the look in
/// the rules' <c>look</c> shape (unset controls omitted), sorted by path.
/// </remarks>
public static class TextureLooksFile
{
    /// <summary>The conventional file name.</summary>
    public const string FileName = "texture-looks.json";

    /// <summary>
    /// Serialises overrides to the <c>texture-looks.json</c> format.
    /// </summary>
    /// <param name="looks">The overrides.</param>
    /// <returns>Indented JSON text.</returns>
    public static string ToJson(IEnumerable<TextureLook> looks)
    {
        ArgumentNullException.ThrowIfNull(looks);

        var document = new TextureLooksDocument
        {
            Textures = [.. looks.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => new TextureLookEntryDocument { Path = l.Path, Look = LookOverridesDocument.From(l.Look) })],
        };
        return JsonSerializer.Serialize(document, ForgeRulesJsonContext.Default.TextureLooksDocument);
    }

    /// <summary>
    /// Reads and validates the <c>texture-looks.json</c> format.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The overrides, by path (later duplicates win).</returns>
    /// <exception cref="JsonException">The JSON is malformed, has an unknown version, an entry without a path or look, or an out-of-range value.</exception>
    public static IReadOnlyDictionary<string, LookOverrides> FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        TextureLooksDocument document = JsonSerializer.Deserialize(json, ForgeRulesJsonContext.Default.TextureLooksDocument)
            ?? throw new JsonException("The texture looks JSON is null.");
        if (document.Version != TextureLooksDocument.CurrentVersion)
        {
            throw new JsonException($"Unsupported texture looks version {document.Version}; expected {TextureLooksDocument.CurrentVersion}.");
        }

        var looks = new Dictionary<string, LookOverrides>(StringComparer.Ordinal);
        int index = 0;
        foreach (TextureLookEntryDocument? entry in document.Textures ?? [])
        {
            index++;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || entry.Look is null)
            {
                throw new JsonException($"Texture look #{index} needs a 'path' and a 'look'.");
            }

            LookOverrides look = entry.Look.ToOverrides();
            look.Validate($"Texture '{entry.Path}'");
            looks[Common.GamePath.Normalize(entry.Path)] = look;
        }

        return looks;
    }

    /// <summary>
    /// Writes every override in <paramref name="store"/> to <paramref name="path"/> (through a temporary file, then moved into place).
    /// </summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="path">The file to write, usually named <see cref="FileName"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The number of overrides written.</returns>
    public static async Task<int> ExportAsync(JobStore store, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IReadOnlyList<TextureLook> looks = await store.ListTextureLooksAsync(ct).ConfigureAwait(false);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{fullPath}.tmp";
        await File.WriteAllTextAsync(tempPath, ToJson(looks), ct).ConfigureAwait(false);
        File.Move(tempPath, fullPath, overwrite: true);
        return looks.Count;
    }

    /// <summary>
    /// Reads <paramref name="path"/> and stores its overrides in one transaction. The whole file is validated before anything is written.
    /// </summary>
    /// <param name="store">The project's job store.</param>
    /// <param name="path">The <c>texture-looks.json</c> file.</param>
    /// <param name="replaceExisting"><see langword="true"/> removes every existing override first; otherwise the file's entries are merged in
    /// (replacing overrides of the same paths).</param>
    /// <param name="ct">Cancels the operation; nothing is written when cancelled.</param>
    /// <returns>The number of overrides imported.</returns>
    /// <exception cref="JsonException">The file is invalid; nothing is written.</exception>
    public static async Task<int> ImportAsync(JobStore store, string path, bool replaceExisting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, LookOverrides> looks = FromJson(json);
        await store.SetTextureLooksAsync(looks, replaceExisting, ct).ConfigureAwait(false);
        return looks.Count;
    }
}
