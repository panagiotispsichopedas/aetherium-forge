using System.Globalization;
using System.Runtime.InteropServices;
using AetheriumForge.Core.Imaging;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// Converts an <see cref="RgbaImage"/> into a <see cref="WriteableBitmap"/> for an <c>Image</c> control. Keeps the view models free of
/// Avalonia bitmap types (and testable without a renderer).
/// </summary>
public sealed class RgbaImageBitmapConverter : IValueConverter
{
    /// <summary>Gets the shared instance.</summary>
    public static RgbaImageBitmapConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is RgbaImage image ? ToBitmap(image) : null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException("Images are display only.");

    /// <summary>Copies <paramref name="image"/> into a new bitmap.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The bitmap.</returns>
    public static WriteableBitmap ToBitmap(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        var rowBytes = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Pixels, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }
}
