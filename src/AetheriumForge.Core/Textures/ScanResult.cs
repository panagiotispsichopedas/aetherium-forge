namespace AetheriumForge.Core.Textures;

/// <summary>
/// The outcome of a texture scan.
/// </summary>
/// <param name="Textures">Every gap-set texture with its decision, sorted by path.</param>
/// <param name="LooseTextureCount">Number of loose <c>*.dds</c> files found under <c>Data\textures</c>.</param>
/// <param name="ModArchiveCount">Number of mod plugin archives that were read as overrides.</param>
/// <param name="BaseArchiveCount">Number of base-game archives (INI list plus Creation Club) that were read as gap sources.</param>
/// <param name="MissingArchives">Archive names that the profile references but that do not exist in <c>Data</c>.</param>
public sealed record ScanResult(
    IReadOnlyList<ScannedTexture> Textures,
    int LooseTextureCount,
    int ModArchiveCount,
    int BaseArchiveCount,
    IReadOnlyList<string> MissingArchives)
{
    /// <summary>
    /// Gets the textures that will be upscaled.
    /// </summary>
    public IEnumerable<ScannedTexture> Queued => Textures.Where(t => !t.Decision.IsSkipped);

    /// <summary>
    /// Gets the textures that the rules skip.
    /// </summary>
    public IEnumerable<ScannedTexture> Skipped => Textures.Where(t => t.Decision.IsSkipped);
}
