using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Services;

/// <summary>
/// The Models page preferences that outlive a session. Kept behind this small interface so the planned move of app preferences into
/// <c>app.db</c> only swaps the implementation.
/// </summary>
public interface IModelsPageSettings
{
    /// <summary>Reads whether the user answered the first-run model prompt with "Later".</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the startup prompt must not be shown again.</returns>
    Task<bool> IsFirstRunPromptDismissedAsync(CancellationToken cancellationToken);

    /// <summary>Stores whether the startup first-run prompt is dismissed.</summary>
    /// <param name="dismissed"><see langword="true"/> to stop showing the prompt at startup.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when stored.</returns>
    Task SetFirstRunPromptDismissedAsync(bool dismissed, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IModelsPageSettings"/> in a small JSON file (by default <c>%LOCALAPPDATA%\AetheriumForge\models-page.json</c>).
/// A missing or unreadable file means "not dismissed", so a damaged file can only bring the prompt back, never hide it.
/// </summary>
public sealed partial class JsonModelsPageSettings : IModelsPageSettings
{
    private const string FirstRunPromptDismissedProperty = "firstRunPromptDismissed";

    private readonly string _path;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonModelsPageSettings"/> class.
    /// </summary>
    /// <param name="path">The settings file.</param>
    /// <param name="logger">Receives read and write failures.</param>
    public JsonModelsPageSettings(string path, ILogger<JsonModelsPageSettings>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<bool> IsFirstRunPromptDismissedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(FirstRunPromptDismissedProperty, out var value)
                    && value.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LogReadFailed(_logger, exception, _path);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SetFirstRunPromptDismissedAsync(bool dismissed, CancellationToken cancellationToken)
    {
        // A unique temporary name: two writes never share a file, and the last move wins.
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                await using (writer.ConfigureAwait(false))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean(FirstRunPromptDismissedProperty, dismissed);
                    writer.WriteEndObject();
                }
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the prompt comes back at the next start. Log it so a read-only profile folder is visible.
            LogWriteFailed(_logger, exception, _path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [LoggerMessage(EventId = 430, Level = LogLevel.Warning, Message = "Reading the Models page settings {Path} failed; using defaults")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 431, Level = LogLevel.Warning, Message = "Writing the Models page settings {Path} failed")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string path);
}
