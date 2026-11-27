namespace AetheriumForge.Core.Rules;

/// <summary>
/// JSON shape of <c>texture-looks.json</c> (§8): the per-texture look overrides of a project, exported separately from the rules.
/// </summary>
internal sealed class TextureLooksDocument
{
    /// <summary>The file format version written by this build.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<TextureLookEntryDocument?>? Textures { get; set; } = [];
}

/// <summary>
/// One entry of <see cref="TextureLooksDocument"/>.
/// </summary>
internal sealed class TextureLookEntryDocument
{
    public string? Path { get; set; }

    public LookOverridesDocument? Look { get; set; }
}
