using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Persists <see cref="GpuProfile"/>s as JSON (IMPLEMENTATION_GUIDE §9, "GPU auto profile"), by default in
/// <c>%LOCALAPPDATA%\AetheriumForge\gpu-profiles.json</c>.
/// </summary>
/// <remarks>
/// <para>A profile is keyed by the adapter's LUID-independent identity (vendor id, device id and name), its driver version and the model
/// family. The LUID changes on every reboot, so it is stored but never matched. Saving a profile replaces any profile for the same
/// hardware and model, including one for an older driver.</para>
/// <para>Writes are atomic (a temporary file in the same folder is moved over the store) and serialised across instances and processes
/// by a named mutex derived from the full path. Reads take no lock and share the file with readers, writers and deletes. The file is a
/// cache of measurements: unreadable files and invalid entries are logged and ignored, never thrown, so the adapter is recalibrated.
/// A file written by a newer schema version is never overwritten.</para>
/// </remarks>
public sealed partial class GpuProfileStore : IDisposable
{
    /// <summary>The schema version written to the file.</summary>
    public const int SchemaVersion = 1;

    private const int MoveAttempts = 10;
    private const int MaxStoredTileSize = 4096;

    private static readonly TimeSpan _moveRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly Mutex _mutex;
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
        _mutex = new Mutex(false, MutexName(FilePath));
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
    /// Lists all valid stored profiles.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profiles; empty when the file does not exist, cannot be read, or has another schema version.</returns>
    public Task<IReadOnlyList<GpuProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Read().Profiles, cancellationToken);

    /// <summary>
    /// Saves a profile, replacing any profile for the same hardware identity and model (whatever its driver version).
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels waiting for other writers.</param>
    /// <returns>
    /// <see langword="true"/> when the profile was written; <see langword="false"/> (with a logged warning) when it is
    /// <see cref="GpuProfile.IsProvisional"/>, invalid, or the file was written by a newer schema version.
    /// </returns>
    public Task<bool> SaveAsync(GpuProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsProvisional)
        {
            LogProvisional(_logger, profile.Model, profile.AdapterName);
            return Task.FromResult(false);
        }

        if (!IsValid(profile))
        {
            LogInvalidProfile(_logger, profile.Model, profile.AdapterName);
            return Task.FromResult(false);
        }

        return Task.Run(
            () => WithLock(
                () =>
                {
                    var current = Read();
                    if (current.NewerSchemaVersion is { } version)
                    {
                        LogNewerSchemaNotOverwritten(_logger, FilePath, version);
                        return false;
                    }

                    Write([.. current.Profiles.Where(existing => !SameSlot(existing, profile)), profile]);
                    LogSaved(_logger, profile.Model, profile.AdapterName, profile.DriverVersion);
                    return true;
                },
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Removes the profile of a model family on an adapter (any driver version).
    /// </summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="model">The model family.</param>
    /// <param name="cancellationToken">Cancels waiting for other writers.</param>
    /// <returns><see langword="true"/> when a profile was removed.</returns>
    public Task<bool> RemoveAsync(GpuAdapterInfo adapter, string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return Task.Run(
            () => WithLock(
                () =>
                {
                    var current = Read();
                    if (current.NewerSchemaVersion is { } version)
                    {
                        LogNewerSchemaNotOverwritten(_logger, FilePath, version);
                        return false;
                    }

                    var kept = current.Profiles
                        .Where(profile => !(profile.MatchesHardware(adapter) && string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (kept.Count == current.Profiles.Count)
                    {
                        return false;
                    }

                    Write(kept);
                    return true;
                },
                cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();

    private static string MutexName(string fullPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        return $"Local\\AetheriumForge.GpuProfileStore.{Convert.ToHexString(hash)}";
    }

    private static bool SameSlot(GpuProfile left, GpuProfile right) =>
        left.VendorId == right.VendorId
        && left.DeviceId == right.DeviceId
        && string.Equals(left.AdapterName, right.AdapterName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase);

    private static bool IsValid(GpuProfile? profile) =>
        profile is not null
        && !string.IsNullOrWhiteSpace(profile.AdapterName)
        && !string.IsNullOrWhiteSpace(profile.Model)
        && !string.IsNullOrWhiteSpace(profile.RecommendedModelFamily)
        && (string.Equals(profile.Precision, GpuProfiler.Fp32, StringComparison.OrdinalIgnoreCase) || string.Equals(profile.Precision, GpuProfiler.Fp16, StringComparison.OrdinalIgnoreCase))
        && profile.TileSize is >= 16 and <= MaxStoredTileSize
        && double.IsFinite(profile.SecondsPerMegapixel)
        && profile.SecondsPerMegapixel > 0
        && Enum.IsDefined(profile.Tier);

    private T WithLock<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            try
            {
                if (WaitHandle.WaitAny([_mutex, cancellationToken.WaitHandle]) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                acquired = true;
            }
            catch (AbandonedMutexException exception)
            {
                // Another process died while writing; the mutex is now ours and the atomic move kept the file consistent.
                acquired = true;
                LogAbandonedMutex(_logger, exception, FilePath);
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                _mutex.ReleaseMutex();
            }
        }
    }

    private StoreContents Read()
    {
        GpuProfileDocument? document;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            document = JsonSerializer.Deserialize(stream, GpuProfileJsonContext.Default.GpuProfileDocument);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return StoreContents.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_logger, exception, FilePath);
            return StoreContents.Empty;
        }
        catch (JsonException exception)
        {
            LogCorrupt(_logger, exception, FilePath);
            return StoreContents.Empty;
        }

        if (document is null)
        {
            LogCorrupt(_logger, null, FilePath);
            return StoreContents.Empty;
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            LogUnsupportedSchema(_logger, FilePath, document.SchemaVersion);
            return document.SchemaVersion > SchemaVersion ? new StoreContents([], document.SchemaVersion) : StoreContents.Empty;
        }

        var entries = document.Profiles ?? [];
        var valid = entries.Where(IsValid).Select(profile => profile!).ToList();
        if (valid.Count != entries.Count)
        {
            LogInvalidEntriesDropped(_logger, FilePath, entries.Count - valid.Count);
        }

        return new StoreContents(valid, null);
    }

    private void Write(IReadOnlyList<GpuProfile> profiles)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var document = new GpuProfileDocument { SchemaVersion = SchemaVersion, Profiles = [.. profiles] };
                JsonSerializer.Serialize(stream, document, GpuProfileJsonContext.Default.GpuProfileDocument);
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetry(temporary);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void MoveWithRetry(string temporary)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                // A reader without FILE_SHARE_DELETE (antivirus, an editor) briefly blocks the replace; retry before giving up.
                LogMoveRetry(_logger, FilePath, attempt, exception.Message);
                Thread.Sleep(_moveRetryDelay);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Saved GPU profile for {Model} on {Adapter} (driver {Driver})")]
    private static partial void LogSaved(ILogger logger, string model, string adapter, string? driver);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "GPU profile file {Path} is not valid JSON; ignoring it, adapters will be recalibrated")]
    private static partial void LogCorrupt(ILogger logger, Exception? exception, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "GPU profile file {Path} has unsupported schema version {Version}; ignoring it, adapters will be recalibrated")]
    private static partial void LogUnsupportedSchema(ILogger logger, string path, int version);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "GPU profile file {Path} could not be read; ignoring it, adapters will be recalibrated")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "GPU profile file {Path}: dropped {Count} invalid entries")]
    private static partial void LogInvalidEntriesDropped(ILogger logger, string path, int count);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "GPU profile file {Path} was written by schema version {Version}; not overwriting it, the profile is not saved")]
    private static partial void LogNewerSchemaNotOverwritten(ILogger logger, string path, int version);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Not saving the provisional GPU profile for {Model} on {Adapter} (it came from an error fallback)")]
    private static partial void LogProvisional(ILogger logger, string model, string adapter);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Not saving an invalid GPU profile for {Model} on {Adapter}")]
    private static partial void LogInvalidProfile(ILogger logger, string model, string adapter);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Another process stopped while writing {Path}; continuing")]
    private static partial void LogAbandonedMutex(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Replacing {Path} failed (attempt {Attempt}: {Reason}); retrying")]
    private static partial void LogMoveRetry(ILogger logger, string path, int attempt, string reason);

    private sealed record StoreContents(IReadOnlyList<GpuProfile> Profiles, int? NewerSchemaVersion)
    {
        public static StoreContents Empty { get; } = new([], null);
    }
}
