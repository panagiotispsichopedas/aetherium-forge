using System.Globalization;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Formats an ETA for people, in the invariant culture: at most two units, for example <c>~45 s</c>, <c>~3 min 20 s</c>,
/// <c>~2 h 15 min</c> or <c>~1 d 4 h</c>.
/// </summary>
public static class EtaFormatter
{
    /// <summary>The text for a missing or negative estimate.</summary>
    public const string Unknown = "unknown";

    /// <summary>Formats an estimate, rounded to the smallest unit shown.</summary>
    /// <param name="eta">The estimate, or <see langword="null"/> when unknown.</param>
    /// <returns>The formatted text, or <see cref="Unknown"/>.</returns>
    public static string Format(TimeSpan? eta)
    {
        if (eta is not { } value || value < TimeSpan.Zero)
        {
            return Unknown;
        }

        var seconds = Round(value.TotalSeconds);
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"~{seconds} s");
        }

        if (seconds < 3600)
        {
            return Join(seconds / 60, "min", seconds % 60, "s");
        }

        var minutes = Round(value.TotalMinutes);
        if (minutes < 1440)
        {
            return Join(minutes / 60, "h", minutes % 60, "min");
        }

        var hours = Round(value.TotalHours);
        return Join(hours / 24, "d", hours % 24, "h");
    }

    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string Join(long major, string majorUnit, long minor, string minorUnit) => minor == 0
        ? string.Create(CultureInfo.InvariantCulture, $"~{major} {majorUnit}")
        : string.Create(CultureInfo.InvariantCulture, $"~{major} {majorUnit} {minor} {minorUnit}");
}
