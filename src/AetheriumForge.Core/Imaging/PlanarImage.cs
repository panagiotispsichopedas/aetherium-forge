namespace AetheriumForge.Core.Imaging;

/// <summary>
/// A float image in planar (CHW) layout with values in 0–1. This is the model input and output format (NCHW with N = 1).
/// </summary>
public sealed class PlanarImage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlanarImage"/> class.
    /// </summary>
    /// <param name="channels">Number of channels; at least 1 (3 for RGB model input).</param>
    /// <param name="width">Width in pixels; at least 1.</param>
    /// <param name="height">Height in pixels; at least 1.</param>
    /// <param name="data">Plane data; length must be <c>channels × width × height</c>. A new zeroed buffer is allocated when <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> has the wrong length.</exception>
    public PlanarImage(int channels, int width, int height, float[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var length = checked(channels * width * height);
        if (data is not null && data.Length != length)
        {
            throw new ArgumentException($"Expected {length} floats for {channels}×{width}×{height}, got {data.Length}.", nameof(data));
        }

        Channels = channels;
        Width = width;
        Height = height;
        Data = data ?? new float[length];
    }

    /// <summary>Gets the number of channels.</summary>
    public int Channels { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the plane data: channel 0 rows, then channel 1 rows, and so on.</summary>
    public float[] Data { get; }

    /// <summary>
    /// Gets the index of a sample in <see cref="Data"/>.
    /// </summary>
    /// <param name="channel">Channel index.</param>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The flat index.</returns>
    public int IndexOf(int channel, int x, int y) => ((channel * Height) + y) * Width + x;
}
