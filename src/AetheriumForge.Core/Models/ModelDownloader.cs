using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Downloads a model from the manifest into the models folder with resume, pause and SHA-256 verification (IMPLEMENTATION_GUIDE §9).
/// </summary>
/// <remarks>
/// <para>
/// The file is streamed to <c>&lt;fileName&gt;.partial</c>, with the first response's validators in <c>&lt;fileName&gt;.partial.meta</c>.
/// An existing partial is re-hashed and resumed with <c>Range</c> and <c>If-Range</c>; a resume is accepted only when the server answers
/// 206 for the exact offset and total size. The download restarts from zero when the server ignores the range, the file changed, or the
/// partial is larger than the file. Pause is checked between buffer chunks: the connection is closed while paused and the range is
/// requested again on resume. A transfer that receives no data for <see cref="IdleTimeout"/> fails with an <see cref="IOException"/>.
/// </para>
/// <para>
/// Cancellation, network errors, stalls and a short but clean transfer keep the partial for a later resume; a file larger than the
/// manifest says or with the wrong SHA-256 deletes it. On success the sidecar <c>&lt;name&gt;.json</c> (the format
/// <see cref="Imaging.ModelCatalog"/> reads) is moved into place first and the model second, so a model never appears without its sidecar.
/// Concurrent calls for the same file in one process share one transfer.
/// </para>
/// <para>
/// <b>Trust boundary.</b> URLs must be release assets of the configured repository, and every response must end (after redirects) on
/// a host in <see cref="ModelSourceOptions.AllowedDownloadHosts"/>. The SHA-256 comes from the manifest in the same release, so integrity
/// rests on write access to the repository's releases; there is no separate signature.
/// </para>
/// </remarks>
public sealed partial class ModelDownloader
{
    /// <summary>The suffix of an incomplete download.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>The suffix of the metadata written next to an incomplete download.</summary>
    public const string PartialMetaSuffix = ".partial.meta";

    /// <summary>The largest licence file accepted, in bytes.</summary>
    public const int MaxLicenceBytes = 1024 * 1024;

    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Task<InstalledModel>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _modelsFolder;
    private readonly ModelSourceOptions _source;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDownloader"/> class that trusts this repository's releases.
    /// </summary>
    /// <param name="http">The HTTP client; it must not impose a total timeout shorter than a large download.</param>
    /// <param name="modelsFolder">The models folder, usually <see cref="Imaging.ModelCatalog.DefaultModelsFolder"/>.</param>
    /// <param name="logger">Receives download events.</param>
    public ModelDownloader(HttpClient http, string modelsFolder, ILogger? logger = null)
        : this(http, modelsFolder, new ModelSourceOptions(), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDownloader"/> class.
    /// </summary>
    /// <param name="http">The HTTP client; it must not impose a total timeout shorter than a large download.</param>
    /// <param name="modelsFolder">The models folder, usually <see cref="Imaging.ModelCatalog.DefaultModelsFolder"/>.</param>
    /// <param name="source">The trusted source: repository and redirect hosts, and <see cref="ModelSourceOptions.DownloadIdleTimeout"/>.</param>
    /// <param name="logger">Receives download events.</param>
    public ModelDownloader(HttpClient http, string modelsFolder, ModelSourceOptions source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        ArgumentNullException.ThrowIfNull(source);
        _http = http;
        _modelsFolder = Path.GetFullPath(modelsFolder);
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        IdleTimeout = source.DownloadIdleTimeout;
    }

    /// <summary>Gets the models folder.</summary>
    public string ModelsFolder => _modelsFolder;

    /// <summary>
    /// Gets or sets how long a transfer may receive no data before it fails with an <see cref="IOException"/> (the partial is kept).
    /// Default <see cref="ModelSourceOptions.DownloadIdleTimeout"/>; <see cref="Timeout.InfiniteTimeSpan"/> disables the watchdog.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; }

    /// <summary>
    /// Downloads, verifies and installs a model, resuming an earlier partial download when there is one. A second call for the same
    /// file while one is running waits for the first (its own progress and pause are not used).
    /// </summary>
    /// <param name="model">The model from the manifest; it is validated like a manifest entry.</param>
    /// <param name="progress">Receives progress, at most about ten times a second plus a final report.</param>
    /// <param name="pause">Pauses the transfer between buffer chunks.</param>
    /// <param name="cancellationToken">Cancels the download; the partial file is kept for a later resume.</param>
    /// <returns>The installed model.</returns>
    /// <exception cref="ModelManifestException"><paramref name="model"/> is not a valid entry for the trusted source.</exception>
    /// <exception cref="UntrustedModelSourceException">A response ended on a host that is not allowed; nothing was written.</exception>
    /// <exception cref="ModelSizeMismatchException">The server sent more bytes than the manifest says; the partial was deleted.</exception>
    /// <exception cref="ModelHashMismatchException">The SHA-256 differs from the manifest; the partial was deleted.</exception>
    /// <exception cref="IOException">The transfer stalled or ended early; the partial is kept for a resume.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached or answered with an error; the partial is kept.</exception>
    public async Task<InstalledModel> DownloadAsync(ManifestModel model, IProgress<DownloadProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ModelManifest.ValidateModel(model, _source);

        var finalPath = Path.Combine(_modelsFolder, model.FileName);
        var completion = new TaskCompletionSource<InstalledModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _inFlight.GetOrAdd(finalPath, completion.Task);
        if (!ReferenceEquals(running, completion.Task))
        {
            LogJoined(_logger, model.Name);
            return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var installed = await DownloadCoreAsync(model, finalPath, progress, pause, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(installed);
            return installed;
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);

            // Observed here so a failure nobody else awaited does not surface as an unobserved task exception.
            _ = completion.Task.Exception;
            throw;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<InstalledModel>>(finalPath, completion.Task));
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsValidResume(HttpResponseMessage response, long offset, long size) =>
        response.StatusCode == HttpStatusCode.PartialContent
        && response.Content.Headers.ContentRange is { From: { } from, Length: { } length }
        && from == offset
        && length == size;

    private static double Rate(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds;

    private static RangeConditionHeaderValue? IfRange(PartialDownloadMeta? meta)
    {
        if (meta?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var parsed) && !parsed.IsWeak)
        {
            return new RangeConditionHeaderValue(parsed);
        }

        return meta?.LastModified is { } lastModified ? new RangeConditionHeaderValue(lastModified) : null;
    }

    private async Task<InstalledModel> DownloadCoreAsync(ManifestModel model, string finalPath, IProgress<DownloadProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsFolder);
        var partialPath = finalPath + PartialSuffix;
        var metaPath = finalPath + PartialMetaSuffix;

        // The licence comes first so an installed model is never without it.
        var licencePath = await EnsureLicenceAsync(model, cancellationToken).ConfigureAwait(false);

        var (sha256, resumed) = await TransferAsync(model, partialPath, metaPath, progress, pause, cancellationToken).ConfigureAwait(false);

        var sidecarPath = Path.ChangeExtension(finalPath, ".json");
        var sidecarTemp = $"{sidecarPath}.{Guid.NewGuid():N}.tmp";
        var sidecar = new InstalledModelSidecar(
            model.DisplayName,
            model.Scale,
            model.Precision?.Trim().ToLowerInvariant(),
            model.Licence,
            model.RecommendedFor,
            sha256,
            model.Size,
            File.GetLastWriteTimeUtc(partialPath),
            model.Tier,
            model.Family);
        try
        {
            await File.WriteAllBytesAsync(sidecarTemp, JsonSerializer.SerializeToUtf8Bytes(sidecar, ModelsJsonContext.Default.InstalledModelSidecar), cancellationToken)
                .ConfigureAwait(false);

            // From here on nothing is cancellable: sidecar first (ModelCatalog ignores a sidecar without a model), then the model.
            File.Move(sidecarTemp, sidecarPath, overwrite: true);
            File.Move(partialPath, finalPath, overwrite: true);
            DeleteIfExists(metaPath);
        }
        finally
        {
            DeleteIfExists(sidecarTemp);
        }

        LogInstalled(_logger, model.Name, finalPath, model.Size);
        return new InstalledModel(model.Name, finalPath, sidecarPath, licencePath, model.Size, sha256, resumed);
    }

    private async Task<(string Sha256, bool Resumed)> TransferAsync(
        ManifestModel model,
        string partialPath,
        string metaPath,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var meta = ReadMeta(metaPath);
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > 0 && meta is not null && (!string.Equals(meta.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase) || meta.Size != model.Size))
        {
            LogPartialForOtherFile(_logger, model.Name);
            DeleteIfExists(partialPath);
            DeleteIfExists(metaPath);
            existing = 0;
            meta = null;
        }

        if (existing > model.Size)
        {
            LogPartialTooLarge(_logger, model.Name, existing, model.Size);
            DeleteIfExists(partialPath);
            DeleteIfExists(metaPath);
            existing = 0;
            meta = null;
        }

        if (existing > 0)
        {
            await HashExistingAsync(partialPath, hash, pause, cancellationToken).ConfigureAwait(false);
            LogResuming(_logger, model.Name, existing, model.Size);
        }

        var restarted = false;
        try
        {
            var file = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                file.SetLength(existing);
                file.Position = existing;
                if (existing < model.Size)
                {
                    restarted = await DownloadLoopAsync(model, file, metaPath, meta, hash, progress, pause, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    progress?.Report(new DownloadProgress(existing, model.Size, 0));
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (UntrustedModelSourceException)
        {
            // Nothing from an untrusted host is kept; an empty partial created for this attempt is removed.
            if (existing == 0 && File.Exists(partialPath) && new FileInfo(partialPath).Length == 0)
            {
                File.Delete(partialPath);
            }

            throw;
        }

        var actualSize = new FileInfo(partialPath).Length;
        if (actualSize > model.Size)
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(metaPath);
            LogVerificationFailed(_logger, model.Name, "size", model.Size.ToString(CultureInfo.InvariantCulture), actualSize.ToString(CultureInfo.InvariantCulture));
            throw new ModelSizeMismatchException($"Model '{model.Name}' is larger than the {model.Size} bytes the manifest says. The partial file was deleted.")
            {
                ModelName = model.Name,
                Expected = model.Size.ToString(CultureInfo.InvariantCulture),
                Actual = actualSize.ToString(CultureInfo.InvariantCulture),
            };
        }

        if (actualSize < model.Size)
        {
            LogShortTransfer(_logger, model.Name, actualSize, model.Size);
            throw new IOException($"The download of '{model.Name}' ended after {actualSize} of {model.Size} bytes. Retry to resume.");
        }

        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(metaPath);
            LogVerificationFailed(_logger, model.Name, "sha256", model.Sha256, actualHash);
            throw new ModelHashMismatchException($"Model '{model.Name}' has SHA-256 {actualHash}; the manifest says {model.Sha256}. The partial file was deleted.")
            {
                ModelName = model.Name,
                Expected = model.Sha256,
                Actual = actualHash,
            };
        }

        return (actualHash, existing > 0 && !restarted);
    }

    /// <summary>Requests and copies until the file is complete or the server ends the body; returns whether it restarted from zero.</summary>
    private async Task<bool> DownloadLoopAsync(
        ManifestModel model,
        FileStream file,
        string metaPath,
        PartialDownloadMeta? meta,
        IncrementalHash hash,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        var restarted = false;
        var done = file.Position;
        var active = Stopwatch.StartNew();
        var state = new CopyState(done, active);
        progress?.Report(new DownloadProgress(done, model.Size, 0));

        while (state.Done < model.Size)
        {
            if (pause.IsPaused)
            {
                active.Stop();
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                LogPaused(_logger, model.Name, state.Done);
                await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                active.Start();
            }

            var offset = state.Done;
            var response = await SendAsync(model.Url, offset, IfRange(meta), cancellationToken).ConfigureAwait(false);
            try
            {
                if (offset > 0 && !IsValidResume(response, offset, model.Size))
                {
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // A server error is not a refused range: keep the partial for the next attempt.
                        response.EnsureSuccessStatusCode();
                    }

                    LogRangeIgnored(_logger, model.Name, (int)response.StatusCode);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        // 416, or a 206 for the wrong range or size: ask again for the whole file.
                        response.Dispose();
                        response = await SendAsync(model.Url, 0, null, cancellationToken).ConfigureAwait(false);
                    }

                    file.SetLength(0);
                    file.Position = 0;
                    hash.GetHashAndReset();
                    state.Done = 0;
                    restarted = true;
                }

                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    meta = new PartialDownloadMeta(model.Url, model.Sha256, model.Size, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
                    await WriteAtomicAsync(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta, ModelsJsonContext.Default.PartialDownloadMeta), cancellationToken)
                        .ConfigureAwait(false);
                }

                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    var paused = await CopyAsync(model, body, file, hash, state, progress, pause, cancellationToken).ConfigureAwait(false);
                    if (!paused)
                    {
                        break;
                    }
                }
            }
            finally
            {
                response.Dispose();
            }
        }

        progress?.Report(new DownloadProgress(state.Done, model.Size, Rate(state.Transferred, active.Elapsed)));
        return restarted;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, long offset, RangeConditionHeaderValue? ifRange, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = ifRange;
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureTrusted(response, url);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void EnsureTrusted(HttpResponseMessage response, Uri requested)
    {
        var final = response.RequestMessage?.RequestUri ?? requested;
        if (!_source.IsTrustedDownloadUrl(final))
        {
            LogUntrustedHost(_logger, requested, final);
            throw new UntrustedModelSourceException($"The download of {requested} ended on untrusted host '{final.Host}'.") { Url = final };
        }
    }

    /// <summary>Copies the body to the file; returns <see langword="true"/> when it stopped because of a pause.</summary>
    private async Task<bool> CopyAsync(
        ManifestModel model,
        Stream body,
        FileStream file,
        IncrementalHash hash,
        CopyState state,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                if (pause.IsPaused)
                {
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    idle.CancelAfter(IdleTimeout);
                    read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token).ConfigureAwait(false);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogStalled(_logger, model.Name, state.Done, IdleTimeout);
                    throw new IOException($"The download of '{model.Name}' received no data for {IdleTimeout.TotalSeconds:0} s. Retry to resume.", exception);
                }

                if (read == 0)
                {
                    return false;
                }

                if (state.Done + read > model.Size)
                {
                    // More bytes than the manifest promises: keep one extra byte so verification reports the mismatch.
                    read = (int)(model.Size - state.Done) + 1;
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    state.Done += read;
                    return false;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                state.Done += read;
                state.Transferred += read;

                if (progress is not null && (state.Active.Elapsed - state.LastReport >= _progressInterval || state.Done == model.Size))
                {
                    state.LastReport = state.Active.Elapsed;
                    progress.Report(new DownloadProgress(state.Done, model.Size, Rate(state.Transferred, state.Active.Elapsed)));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task HashExistingAsync(string path, IncrementalHash hash, PauseToken pause, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private PartialDownloadMeta? ReadMeta(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(metaPath), ModelsJsonContext.Default.PartialDownloadMeta);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Without validators the resume still works; the SHA-256 check catches a changed file.
            LogMetaUnreadable(_logger, exception, metaPath);
            return null;
        }
    }

    private async Task<string?> EnsureLicenceAsync(ManifestModel model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.LicenceFile) || model.LicenceUrl is null)
        {
            return null;
        }

        var path = Path.Combine(_modelsFolder, model.LicenceFile);
        if (File.Exists(path))
        {
            return path;
        }

        using var response = await SendAsync(model.LicenceUrl, 0, null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxLicenceBytes)
        {
            throw new InvalidDataException($"The licence file {model.LicenceUrl} is larger than {MaxLicenceBytes} bytes.");
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxLicenceBytes)
                {
                    throw new InvalidDataException($"The licence file {model.LicenceUrl} is larger than {MaxLicenceBytes} bytes.");
                }

                memory.Write(buffer, 0, read);
            }

            await WriteAtomicAsync(path, memory.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        LogLicenceDownloaded(_logger, model.LicenceFile, path);
        return path;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temp);
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Resuming {Model} at {Offset} of {Total} bytes")]
    private static partial void LogResuming(ILogger logger, string model, long offset, long total);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Partial download of {Model} is {Length} bytes, larger than {Total}; restarting")]
    private static partial void LogPartialTooLarge(ILogger logger, string model, long length, long total);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Server did not honour the range for {Model} (HTTP {StatusCode}); restarting from zero")]
    private static partial void LogRangeIgnored(ILogger logger, string model, int statusCode);

    [LoggerMessage(EventId = 203, Level = LogLevel.Information, Message = "Download of {Model} paused at {Bytes} bytes; connection closed")]
    private static partial void LogPaused(ILogger logger, string model, long bytes);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Verification of {Model} failed: {Check} expected {Expected}, got {Actual}")]
    private static partial void LogVerificationFailed(ILogger logger, string model, string check, string expected, string actual);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information, Message = "Installed {Model} at {Path} ({Size} bytes)")]
    private static partial void LogInstalled(ILogger logger, string model, string path, long size);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "Downloaded licence {LicenceFile} to {Path}")]
    private static partial void LogLicenceDownloaded(ILogger logger, string licenceFile, string path);

    [LoggerMessage(EventId = 207, Level = LogLevel.Information, Message = "A download of {Model} is already running; waiting for it")]
    private static partial void LogJoined(ILogger logger, string model);

    [LoggerMessage(EventId = 208, Level = LogLevel.Warning, Message = "Partial download of {Model} belongs to a different file; restarting")]
    private static partial void LogPartialForOtherFile(ILogger logger, string model);

    [LoggerMessage(EventId = 209, Level = LogLevel.Warning, Message = "Download of {Model} ended early at {Length} of {Total} bytes; the partial is kept")]
    private static partial void LogShortTransfer(ILogger logger, string model, long length, long total);

    [LoggerMessage(EventId = 210, Level = LogLevel.Warning, Message = "Download of {Model} stalled at {Bytes} bytes for {Timeout}; the partial is kept")]
    private static partial void LogStalled(ILogger logger, string model, long bytes, TimeSpan timeout);

    [LoggerMessage(EventId = 211, Level = LogLevel.Error, Message = "Rejected download of {Requested}: it ended on untrusted {Final}")]
    private static partial void LogUntrustedHost(ILogger logger, Uri requested, Uri final);

    [LoggerMessage(EventId = 212, Level = LogLevel.Warning, Message = "Ignoring unreadable partial metadata {Path}")]
    private static partial void LogMetaUnreadable(ILogger logger, Exception exception, string path);

    /// <summary>Byte counters shared by the request loop and the copy loop.</summary>
    private sealed class CopyState(long done, Stopwatch active)
    {
        public long Done { get; set; } = done;

        public long Transferred { get; set; }

        public Stopwatch Active { get; } = active;

        public TimeSpan LastReport { get; set; } = -_progressInterval;
    }
}
