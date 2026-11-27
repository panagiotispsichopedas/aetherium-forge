using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Upscales an RGB image with an AI model (IMPLEMENTATION_GUIDE §9).
/// </summary>
public interface IUpscaler : IDisposable
{
    /// <summary>Gets the model name, for example <c>realesrgan-x4plus</c>.</summary>
    string ModelName { get; }

    /// <summary>Gets the integer scale factor of the model (output = input × scale).</summary>
    int Scale { get; }

    /// <summary>
    /// Upscales <paramref name="rgb"/> by <see cref="Scale"/>.
    /// </summary>
    /// <param name="rgb">Three-channel planar input, values 0–1.</param>
    /// <param name="progress">Optional per-tile progress.</param>
    /// <param name="pause">Pause gate, checked between tiles.</param>
    /// <param name="cancellationToken">Cancels between tiles.</param>
    /// <returns>Three-channel planar output of size input × <see cref="Scale"/>, values clamped to 0–1.</returns>
    Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken);
}

/// <summary>
/// Tile progress inside one upscale call.
/// </summary>
/// <param name="Done">Tiles finished.</param>
/// <param name="Total">Tiles in the image.</param>
public readonly record struct TileProgress(int Done, int Total);
