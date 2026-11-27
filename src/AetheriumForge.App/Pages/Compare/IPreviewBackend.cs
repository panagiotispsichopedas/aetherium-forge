using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// What the preview engine reads from and upscales with: the open project's texture source, the DDS codec and the installed models.
/// </summary>
public interface IPreviewBackend
{
    /// <summary>Gets the open project's texture source, or <see langword="null"/> when no project is open.</summary>
    ITextureSource? Source { get; }

    /// <summary>Gets the DDS codec (texconv), or <see langword="null"/> when texconv is not available.</summary>
    ITextureCodec? Codec { get; }

    /// <summary>Gets the work folder for temporary decode files, or <see langword="null"/> when no project is open.</summary>
    string? WorkFolder { get; }

    /// <summary>Gets the on-disk preview cache folder (<c>&lt;project&gt;\work\preview</c>), or <see langword="null"/> to cache in memory only.</summary>
    string? DiskCacheFolder { get; }

    /// <summary>Gets the installed models.</summary>
    IReadOnlyList<ModelInfo> Models { get; }

    /// <summary>Gets the upscaler for <paramref name="model"/> (GPU). The backend owns it.</summary>
    /// <param name="model">The model name.</param>
    /// <returns>The upscaler.</returns>
    IUpscaler GetUpscaler(string model);
}
