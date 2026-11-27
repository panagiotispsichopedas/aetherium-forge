namespace AetheriumForge.Core.Textures;

/// <summary>
/// Scan phases, in the order they run.
/// </summary>
public enum ScanPhase
{
    /// <summary>Enumerating loose texture files in <c>Data\textures</c>.</summary>
    LooseFiles,

    /// <summary>Reading mod plugin archives (overrides).</summary>
    ModArchives,

    /// <summary>Reading base-game archives (gap sources).</summary>
    BaseArchives,

    /// <summary>Reading DDS headers and applying rules to the gap set.</summary>
    Headers,
}

/// <summary>
/// Progress report emitted by <see cref="TextureScanner"/>.
/// </summary>
/// <param name="Phase">The current phase.</param>
/// <param name="Done">Items finished in this phase.</param>
/// <param name="Total">Items in this phase, or 0 when unknown.</param>
public readonly record struct ScanProgress(ScanPhase Phase, int Done, int Total);
