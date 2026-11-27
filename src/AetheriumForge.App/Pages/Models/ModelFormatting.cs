using System.Globalization;

namespace AetheriumForge.App.Pages.Models;

/// <summary>
/// Text formatting shared by the Models page.
/// </summary>
internal static class ModelFormatting
{
    private static readonly string[] _units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count with binary units, for example <c>64.0 MB</c>.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The text.</returns>
    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < _units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes:0} {_units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes:0.0} {_units[unit]}");
    }

    /// <summary>Formats a precision name for display: <c>fp16</c> → <c>FP16</c>.</summary>
    /// <param name="precision">The precision, or <see langword="null"/>.</param>
    /// <returns>The text, or an em dash.</returns>
    public static string Precision(string? precision) => string.IsNullOrWhiteSpace(precision) ? "—" : precision.ToUpperInvariant();

    /// <summary>Formats a manifest tier for display: <c>low</c> → <c>Low</c>.</summary>
    /// <param name="tier">The tier, or <see langword="null"/>.</param>
    /// <returns>The text, or an em dash.</returns>
    public static string Tier(string? tier) => string.IsNullOrWhiteSpace(tier)
        ? "—"
        : string.Concat(tier[..1].ToUpperInvariant(), tier[1..].ToLowerInvariant());

    /// <summary>Formats a number with invariant culture and the given format.</summary>
    /// <param name="value">The value.</param>
    /// <param name="format">The format.</param>
    /// <returns>The text.</returns>
    public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
