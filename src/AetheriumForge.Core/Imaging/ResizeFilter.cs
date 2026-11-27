namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Resampling filters for <see cref="ImageOps.Resize"/>, defined as in Pillow.
/// </summary>
public enum ResizeFilter
{
    /// <summary>Lanczos windowed sinc with a = 3 (Pillow <c>LANCZOS</c>). Used for colour and normal maps.</summary>
    Lanczos3,

    /// <summary>Cubic convolution with a = -0.5 and support 2 (Pillow <c>BICUBIC</c>). Used for alpha.</summary>
    Bicubic,
}
