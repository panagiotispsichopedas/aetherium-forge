namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Tiling options for <see cref="TiledUpscaler"/> (IMPLEMENTATION_GUIDE §9).
/// </summary>
public sealed record TilingOptions
{
    /// <summary>Gets the tile size in source pixels, excluding overlap. Default 384.</summary>
    public int TileSize { get; init; } = 384;

    /// <summary>Gets the overlap in source pixels added on each inner tile edge. Default 24.</summary>
    public int Overlap { get; init; } = 24;

    /// <summary>Gets the smallest tile size the out-of-memory fallback may reduce to. Default 64.</summary>
    public int MinTileSize { get; init; } = 64;

    /// <summary>Gets a value indicating whether seams are blended linearly across the overlap instead of cropped hard.</summary>
    public bool Feather { get; init; }
}
