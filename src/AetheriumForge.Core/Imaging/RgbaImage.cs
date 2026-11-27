namespace AetheriumForge.Core.Imaging;

/// <summary>
/// An 8-bit RGBA image, row-major, 4 bytes per pixel, no row padding. This is the interchange format with texconv.
/// </summary>
public sealed class RgbaImage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RgbaImage"/> class.
    /// </summary>
    /// <param name="width">Width in pixels; at least 1.</param>
    /// <param name="height">Height in pixels; at least 1.</param>
    /// <param name="pixels">Pixel bytes; length must be <c>width × height × 4</c>. A new zeroed buffer is allocated when <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> has the wrong length.</exception>
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var length = checked(width * height * 4);
        if (pixels is not null && pixels.Length != length)
        {
            throw new ArgumentException($"Expected {length} bytes for {width}×{height} RGBA, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[length];
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixel bytes (R, G, B, A per pixel).</summary>
    public byte[] Pixels { get; }
}
