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
/// Loads and saves <see cref="AppSettings"/>. Saves are atomic (a temporary file replaced in one move); a missing, unreadable or corrupt file
/// loads as the defaults and is logged, so a bad file never stops the app.
/// </summary>
public sealed partial class AppSettingsStore
{
    /// <summary>The settings file name.</summary>
    public const string FileName = "app-settings.json";

    /// <summary>How many recent projects are kept.</summary>
    public const int MaxRecentProjects = 10;

    private readonly ILogger<AppSettingsStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="AppSettingsStore"/> class.</summary>
    /// <param name="filePath">The settings file.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    public AppSettingsStore(string filePath, ILogger<AppSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = Path.GetFullPath(filePath);
        _logger = logger ?? NullLogger<AppSettingsStore>.Instance;
    }

    /// <summary>Gets the settings file.</summary>
    public string FilePath { get; }

    /// <summary>Loads the settings.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The settings, or the defaults when the file is missing or cannot be read.</returns>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
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

    /// <summary>Saves the settings atomically.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is replaced.</returns>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = $"{FilePath}.{Environment.ProcessId}.tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
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
        RecentProjects = [.. (settings?.RecentProjects ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecentProjects)],
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The app settings file {Path} is corrupt; using the defaults")]
    private static partial void LogCorrupt(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The app settings file {Path} cannot be read; using the defaults")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);
}

/// <summary>Source-generated JSON for <see cref="AppSettings"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
