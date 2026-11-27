using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// Who holds a lock: written by the holder for diagnostics and read back by a process that finds the lock taken.
/// </summary>
/// <param name="ProcessId">The holder's process id.</param>
/// <param name="ProcessName">The holder's process name, for example <c>AetheriumForge</c>.</param>
/// <param name="Description">What the holder is, for example <c>Aetherium Forge</c> or <c>aetherium-forge scan</c>.</param>
/// <param name="StartedAt">When the lock was taken (UTC).</param>
/// <param name="Purpose">What the lock is held for, for example <c>a calibration</c>; <see langword="null"/> for project locks.</param>
public sealed record LockOwnerInfo(int ProcessId, string ProcessName, string Description, DateTimeOffset StartedAt, string? Purpose = null)
{
    private const string PidKey = "pid";
    private const string ProcessKey = "process";
    private const string OwnerKey = "owner";
    private const string StartedKey = "started";
    private const string PurposeKey = "purpose";

    /// <summary>Gets the current process's owner record.</summary>
    /// <param name="description">What this process is.</param>
    /// <param name="purpose">What the lock is held for, or <see langword="null"/>.</param>
    /// <returns>The owner record.</returns>
    public static LockOwnerInfo ForCurrentProcess(string description, string? purpose = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        using var process = Process.GetCurrentProcess();
        return new LockOwnerInfo(Environment.ProcessId, process.ProcessName, Sanitize(description), DateTimeOffset.UtcNow, purpose is null ? null : Sanitize(purpose));
    }

    /// <summary>Serializes the record as <c>key=value</c> lines.</summary>
    /// <returns>The text written into the lock or owner file.</returns>
    public string Serialize()
    {
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{PidKey}={ProcessId}").Append('\n')
            .Append(CultureInfo.InvariantCulture, $"{ProcessKey}={Sanitize(ProcessName)}").Append('\n')
            .Append(CultureInfo.InvariantCulture, $"{OwnerKey}={Sanitize(Description)}").Append('\n')
            .Append(CultureInfo.InvariantCulture, $"{StartedKey}={StartedAt.ToString("O", CultureInfo.InvariantCulture)}").Append('\n');
        if (Purpose is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{PurposeKey}={Sanitize(Purpose)}").Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Parses text written by <see cref="Serialize"/>.</summary>
    /// <param name="text">The file contents.</param>
    /// <returns>The record, or <see langword="null"/> when the text is empty or malformed (for example a half-written file).</returns>
    public static LockOwnerInfo? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        if (!values.TryGetValue(PidKey, out var pidText) || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return null;
        }

        var started = values.TryGetValue(StartedKey, out var startedText)
            && DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return new LockOwnerInfo(
            pid,
            values.GetValueOrDefault(ProcessKey) ?? string.Empty,
            values.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : "another Aetherium Forge process",
            started,
            values.GetValueOrDefault(PurposeKey));
    }

    /// <summary>Tells whether the recorded process still runs (best effort: a reused PID reads as alive).</summary>
    /// <returns><see langword="true"/> when a process with <see cref="ProcessId"/> exists.</returns>
    public bool IsProcessAlive()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws ArgumentException when no process has that id.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // An elevated process cannot be queried, but it exists.
            return true;
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
