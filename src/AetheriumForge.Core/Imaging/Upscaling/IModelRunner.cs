namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Runs a super-resolution model on one tile (IMPLEMENTATION_GUIDE §9). Implementations are synchronous and not thread-safe;
/// callers serialise access (one GPU worker per adapter).
/// </summary>
public interface IModelRunner : IDisposable
{
    /// <summary>Gets the integer scale factor of the model (output = input × scale).</summary>
    int Scale { get; }

    /// <summary>
    /// Runs the model on one tile.
    /// </summary>
    /// <param name="tile">Three-channel planar tile (NCHW with N = 1), values 0–1.</param>
    /// <returns>The model output: three channels, <c>tile.Width × Scale</c> by <c>tile.Height × Scale</c>. Values are not clamped.</returns>
    /// <exception cref="VideoMemoryExhaustedException">The device ran out of memory for this tile size.</exception>
    PlanarImage Run(PlanarImage tile);
}
