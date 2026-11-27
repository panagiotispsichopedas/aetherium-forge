using System.Security.Cryptography;
using System.Text.Json;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Models;

/// <summary>
/// The state of a model on the Models page.
/// </summary>
public enum ModelState
{
    /// <summary>In the manifest, not installed.</summary>
    NotInstalled,

    /// <summary>Installed and identical to the manifest.</summary>
    Installed,

    /// <summary>Installed, but the newest release publishes a file with a different SHA-256 under the same name.</summary>
    UpdateAvailable,

    /// <summary>Installed by the user; not in the manifest.</summary>
    Custom,
}

/// <summary>
/// One row of the Models page: the manifest entry merged with what is installed.
/// </summary>
/// <param name="Name">The model name.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="State">The state.</param>
/// <param name="Available">The manifest entry, or <see langword="null"/> for a custom model or when offline.</param>
/// <param name="Installed">The installed model, or <see langword="null"/>.</param>
/// <param name="InstalledSha256">The SHA-256 of the installed file when known, lowercase hex.</param>
/// <param name="HasPartialDownload"><see langword="true"/> when an interrupted download can be resumed.</param>
public sealed record ModelEntry(string Name, string DisplayName, ModelState State, ManifestModel? Available, ModelInfo? Installed, string? InstalledSha256, bool HasPartialDownload);

/// <summary>
/// The service behind the Models page: lists available and installed models, downloads and deletes them (IMPLEMENTATION_GUIDE §9).
/// </summary>
public sealed partial class ModelLibrary
{
    private readonly string _modelsFolder;
    private readonly ModelManifestClient _client;
    private readonly ModelDownloader _downloader;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelLibrary"/> class.
    /// </summary>
    /// <param name="modelsFolder">The models folder; must be the downloader's folder.</param>
    /// <param name="client">Fetches the manifest.</param>
    /// <param name="downloader">Downloads models.</param>
    /// <param name="logger">Receives library events.</param>
    public ModelLibrary(string modelsFolder, ModelManifestClient client, ModelDownloader downloader, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(downloader);
        _modelsFolder = Path.GetFullPath(modelsFolder);
        _client = client;
        _downloader = downloader;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the models folder.</summary>
    public string ModelsFolder => _modelsFolder;

    /// <summary>Gets the manifest used by the last successful <see cref="ListAsync"/>, or <see langword="null"/>.</summary>
    public ModelManifest? LastManifest { get; private set; }

    /// <summary>
    /// Lists manifest models and installed models. When no manifest can be obtained, installed models are still listed: those with a
    /// recorded SHA-256 (downloaded by the app) as <see cref="ModelState.Installed"/>, the rest as <see cref="ModelState.Custom"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Manifest models in manifest order, then custom models by name.</returns>
    public async Task<IReadOnlyList<ModelEntry>> ListAsync(CancellationToken cancellationToken)
    {
        ModelManifest? manifest;
        try
        {
            manifest = await _client.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            LastManifest = manifest;
        }
        catch (Exception exception) when (exception is ModelSourceUnavailableException or ModelManifestException)
        {
            // The installed models are still useful without the manifest (offline, or a schema this build does not read).
            LogManifestUnavailable(_logger, exception);
            manifest = null;
        }

        var installed = ModelCatalog.Discover(_modelsFolder, _logger);
        var byName = installed.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);
        var entries = new List<ModelEntry>();

        foreach (var available in manifest?.Models ?? [])
        {
            var partial = File.Exists(Path.Combine(_modelsFolder, available.FileName + ModelDownloader.PartialSuffix));
            if (!byName.Remove(available.Name, out var local))
            {
                entries.Add(new ModelEntry(available.Name, available.DisplayName ?? available.Name, ModelState.NotInstalled, available, null, null, partial));
                continue;
            }

            var sha = ReadRecordedSha256(local.Path) ?? await ComputeSha256Async(local.Path, cancellationToken).ConfigureAwait(false);
            var state = string.Equals(sha, available.Sha256, StringComparison.OrdinalIgnoreCase) ? ModelState.Installed : ModelState.UpdateAvailable;
            entries.Add(new ModelEntry(available.Name, local.DisplayName, state, available, local, sha, partial));
        }

        foreach (var local in byName.Values.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sha = ReadRecordedSha256(local.Path);
            var state = manifest is null && sha is not null ? ModelState.Installed : ModelState.Custom;
            entries.Add(new ModelEntry(local.Name, local.DisplayName, state, null, local, sha, false));
        }

        return entries;
    }

    /// <summary>
    /// Downloads and installs a model. See <see cref="ModelDownloader.DownloadAsync"/>.
    /// </summary>
    /// <param name="model">The manifest entry.</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="pause">Pauses the transfer.</param>
    /// <param name="cancellationToken">Cancels; the partial file is kept.</param>
    /// <returns>The installed model.</returns>
    public Task<InstalledModel> DownloadAsync(ManifestModel model, IProgress<DownloadProgress>? progress, PauseToken pause, CancellationToken cancellationToken) =>
        _downloader.DownloadAsync(model, progress, pause, cancellationToken);

    /// <summary>
    /// Deletes an installed model: its <c>.onnx</c>, its sidecar and any partial download. The shared licence file is kept.
    /// </summary>
    /// <param name="name">The model name (file name without <c>.onnx</c>).</param>
    /// <param name="cancellationToken">Cancels before deleting.</param>
    /// <returns><see langword="true"/> when anything was deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a plain file name.</exception>
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();
        var modelPath = Path.Combine(_modelsFolder, name + ".onnx");
        var deleted = false;
        foreach (var path in new[] { modelPath, Path.ChangeExtension(modelPath, ".json"), modelPath + ModelDownloader.PartialSuffix, modelPath + ModelDownloader.PartialMetaSuffix })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
        }

        if (deleted)
        {
            LogDeleted(_logger, name);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Computes the SHA-256 of an installed model.
    /// </summary>
    /// <param name="name">The model name.</param>
    /// <param name="cancellationToken">Cancels the hash.</param>
    /// <returns>The SHA-256, lowercase hex.</returns>
    /// <exception cref="FileNotFoundException">The model is not installed.</exception>
    public Task<string> ComputeInstalledSha256Async(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        var path = Path.Combine(_modelsFolder, name + ".onnx");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Model '{name}' is not installed.", path);
        }

        return ComputeSha256Async(path, cancellationToken);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ModelManifest.IsSafeFileName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid model name.", nameof(name));
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
    }

    private string? ReadRecordedSha256(string modelPath)
    {
        var sidecarPath = Path.ChangeExtension(modelPath, ".json");
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            var sidecar = JsonSerializer.Deserialize(File.ReadAllBytes(sidecarPath), ModelsJsonContext.Default.InstalledModelSidecar);
            var file = new FileInfo(modelPath);

            // The recorded hash only counts while the file keeps the recorded size and last write time; otherwise it is re-hashed.
            return sidecar?.Sha256 is { Length: 64 } sha && sidecar.Size == file.Length && sidecar.LastWriteTimeUtc == file.LastWriteTimeUtc
                ? sha.ToLowerInvariant()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LogSidecarUnreadable(_logger, exception, sidecarPath);
            return null;
        }
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Warning, Message = "Model list unavailable or unreadable; showing installed models only")]
    private static partial void LogManifestUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "Deleted model {Model}")]
    private static partial void LogDeleted(ILogger logger, string model);

    [LoggerMessage(EventId = 302, Level = LogLevel.Warning, Message = "Could not read the recorded hash from {SidecarPath}; hashing the file")]
    private static partial void LogSidecarUnreadable(ILogger logger, Exception exception, string sidecarPath);
}
