using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Persists <see cref="GpuProfile"/>s as JSON (IMPLEMENTATION_GUIDE §9, "GPU auto profile"), by default in
/// <c>%LOCALAPPDATA%\AetheriumForge\gpu-profiles.json</c>.
/// </summary>
/// <remarks>
/// A profile is keyed by the adapter's LUID-independent identity (vendor id, device id and name), its driver version and the model
/// family. The LUID changes on every reboot, so it is stored but never matched. Saving a profile replaces any profile for the same
/// hardware and model, including one for an older driver. Writes go to a temporary file that is then moved over the store, so a crash
/// never leaves a half-written file. One instance serialises its own reads and writes.
/// </remarks>
public sealed partial class GpuProfileStore : IDisposable
{
    /// <summary>The schema version written to the file.</summary>
    public const int SchemaVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuProfileStore"/> class.
    /// </summary>
    /// <param name="filePath">The JSON file; defaults to <see cref="DefaultFilePath"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public GpuProfileStore(string? filePath = null, ILogger<GpuProfileStore>? logger = null)
    {
        FilePath = Path.GetFullPath(string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath);
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Gets the default file: <c>%LOCALAPPDATA%\AetheriumForge\gpu-profiles.json</c>.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "gpu-profiles.json");

    /// <summary>Gets the full path of the JSON file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="profile"/> cannot be used for <paramref name="adapter"/>: it is missing,
    /// was calibrated on different hardware, or the driver version changed.
    /// </summary>
    /// <param name="profile">The stored profile, or <see langword="null"/>.</param>
    /// <param name="adapter">The current adapter.</param>
    /// <returns><see langword="true"/> when the adapter must be recalibrated.</returns>
    public static bool NeedsRecalibration(GpuProfile? profile, GpuAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return profile is null
            || !profile.MatchesHardware(adapter)
            || !string.Equals(profile.DriverVersion, adapter.DriverVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the profile of a model family on an adapter, matching vendor id, device id, name, driver version and model; the LUID is ignored.
    /// The returned profile carries the adapter's current <see cref="GpuAdapterInfo.Luid"/> and <see cref="GpuAdapterInfo.DirectMLDeviceId"/>.
    /// </summary>
    /// <param name="adapter">The current adapter.</param>
    /// <param name="model">The model family (see <see cref="GpuProfiler.GetModelFamily"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profile, or <see langword="null"/> when there is none for this hardware, driver and model.</returns>
    public async Task<GpuProfile?> FindAsync(GpuAdapterInfo adapter, string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var profiles = await ListAsync(cancellationToken).ConfigureAwait(false);
        var match = profiles.FirstOrDefault(profile =>
            profile.MatchesHardware(adapter)
            && string.Equals(profile.DriverVersion, adapter.DriverVersion, StringComparison.Ordinal)
            && string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match with { AdapterLuid = adapter.Luid, DirectMLDeviceId = adapter.DirectMLDeviceId };
    }

    /// <summary>
    /// Lists all stored profiles.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profiles; empty when the file does not exist.</returns>
    public async Task<IReadOnlyList<GpuProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves a profile, replacing any profile for the same hardware identity and model (whatever its driver version).
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public async Task SaveAsync(GpuProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = profiles.Where(existing => !SameSlot(existing, profile)).Append(profile).ToList();
            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            LogSaved(_logger, profile.Model, profile.AdapterName, profile.DriverVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the profile of a model family on an adapter (any driver version).
    /// </summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="model">The model family.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a profile was removed.</returns>
    public async Task<bool> RemoveAsync(GpuAdapterInfo adapter, string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var kept = profiles
                .Where(profile => !(profile.MatchesHardware(adapter) && string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (kept.Count == profiles.Count)
            {
                return false;
            }

            await WriteAsync(kept, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static bool SameSlot(GpuProfile left, GpuProfile right) =>
        left.VendorId == right.VendorId
        && left.DeviceId == right.DeviceId
        && string.Equals(left.AdapterName, right.AdapterName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<GpuProfile>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        GpuProfileDocument? document;
        var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                document = await JsonSerializer.DeserializeAsync(stream, GpuProfileJsonContext.Default.GpuProfileDocument, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                // The file is a cache of measurements: a corrupt file only means the adapters get recalibrated.
                LogCorrupt(_logger, exception, FilePath);
                return [];
            }
        }

        if (document is null || document.SchemaVersion != SchemaVersion)
        {
            LogUnsupportedSchema(_logger, FilePath, document?.SchemaVersion ?? 0);
            return [];
        }

        return document.Profiles;
    }

    private async Task WriteAsync(IReadOnlyList<GpuProfile> profiles, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var document = new GpuProfileDocument { SchemaVersion = SchemaVersion, Profiles = [.. profiles] };
                await JsonSerializer.SerializeAsync(stream, document, GpuProfileJsonContext.Default.GpuProfileDocument, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Saved GPU profile for {Model} on {Adapter} (driver {Driver})")]
    private static partial void LogSaved(ILogger logger, string model, string adapter, string? driver);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "GPU profile file {Path} is not valid JSON; ignoring it, adapters will be recalibrated")]
    private static partial void LogCorrupt(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "GPU profile file {Path} has unsupported schema version {Version}; ignoring it, adapters will be recalibrated")]
    private static partial void LogUnsupportedSchema(ILogger logger, string path, int version);
}
