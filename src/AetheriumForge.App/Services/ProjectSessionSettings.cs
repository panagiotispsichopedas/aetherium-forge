using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Services;

/// <summary>
/// The per-user app settings kept in <c>%LOCALAPPDATA%\AetheriumForge\app-settings.json</c>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Gets the recently opened project folders, most recent first.</summary>
    public IReadOnlyList<string> RecentProjects { get; init; } = [];
}

/// <summary>
/// Loads and saves the per-user <see cref="AppSettings"/>. A seam so the JSON file can later move into the app-level <c>app.db</c>.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>Gets where the settings are kept (for messages and logs).</summary>
    string FilePath { get; }

    /// <summary>Loads the settings.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The settings, or the defaults when none are stored or they cannot be read.</returns>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the settings are stored.</returns>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Saves are atomic (a temporary file replaced in one move); a missing, unreadable or corrupt file
/// loads as the defaults and is logged, so a bad file never stops the app.
/// </summary>
public sealed partial class AppSettingsStore : IAppSettingsStore, IDisposable
{
    /// <summary>The settings file name.</summary>
    public const string FileName = "app-settings.json";

    /// <summary>How many recent projects are kept.</summary>
    public const int MaxRecentProjects = 10;

    /// <summary>How many times the final move is tried.</summary>
    private const int MoveAttempts = 8;

    private readonly ILogger<AppSettingsStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="AppSettingsStore"/> class.</summary>
    /// <param name="filePath">The settings file.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    public AppSettingsStore(string filePath, ILogger<AppSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = Path.GetFullPath(filePath);
        _logger = logger ?? NullLogger<AppSettingsStore>.Instance;
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>Tells whether a stored recent entry is a usable, fully qualified folder path.</summary>
    /// <param name="folder">The entry.</param>
    /// <returns><see langword="true"/> when it can be opened as a project folder.</returns>
    public static bool IsValidFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(folder);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var stream = File.OpenRead(FilePath);
            await using (stream.ConfigureAwait(false))
            {
                var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
                return Normalise(settings);
            }
        }
        catch (JsonException ex)
        {
            LogCorrupt(_logger, ex, FilePath);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            LogUnreadable(_logger, ex, FilePath);
            return new AppSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            LogUnreadable(_logger, ex, FilePath);
            return new AppSettings();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // A unique temporary name per call, so two app instances never write the same temporary file.
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
            }

            await ReplaceWithRetryAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Replaces the settings file with <paramref name="temporary"/>. Another app instance replacing the same file at the same moment makes
    /// Windows deny the move briefly, so it is retried a few times before the error is reported.
    /// </summary>
    private async Task ReplaceWithRetryAsync(string temporary, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
            {
                LogMoveRetried(_logger, ex, FilePath, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Moves <paramref name="projectFolder"/> to the front of the recent list (case-insensitive), keeping at most <see cref="MaxRecentProjects"/>.</summary>
    /// <param name="recent">The current list.</param>
    /// <param name="projectFolder">The project just opened.</param>
    /// <returns>The new list.</returns>
    public static IReadOnlyList<string> PushRecent(IReadOnlyList<string> recent, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);

        return [.. new[] { projectFolder }
            .Concat(recent.Where(r => !string.Equals(r, projectFolder, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxRecentProjects)];
    }

    private static AppSettings Normalise(AppSettings? settings) => new()
    {
        RecentProjects = [.. (settings?.RecentProjects ?? []).Where(IsValidFolder).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecentProjects)],
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The app settings file {Path} is corrupt; using the defaults")]
    private static partial void LogCorrupt(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Replacing the app settings file {Path} failed (attempt {Attempt}); retrying")]
    private static partial void LogMoveRetried(ILogger logger, Exception exception, string path, int attempt);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The app settings file {Path} cannot be read; using the defaults")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);
}

/// <summary>Source-generated JSON for <see cref="AppSettings"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
