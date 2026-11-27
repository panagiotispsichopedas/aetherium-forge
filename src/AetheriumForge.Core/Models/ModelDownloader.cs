using System.Buffers;
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
/// The file is streamed to <c>&lt;fileName&gt;.partial</c>. An existing partial is re-hashed and resumed with an HTTP <c>Range</c> request;
/// the download restarts from zero when the server ignores the range or the partial is larger than the file. Pause is checked between
/// buffer chunks. Cancellation and network errors leave the partial for a later resume; a size or hash mismatch deletes it. On success
/// the file is moved into place atomically and a sidecar <c>&lt;name&gt;.json</c> is written in the format <see cref="Imaging.ModelCatalog"/> reads.
/// </remarks>
public sealed partial class ModelDownloader
{
    /// <summary>The suffix of an incomplete download.</summary>
    public const string PartialSuffix = ".partial";

    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly string _modelsFolder;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDownloader"/> class.
    /// </summary>
    /// <param name="http">The HTTP client; it must not impose a total timeout shorter than a large download.</param>
    /// <param name="modelsFolder">The models folder, usually <see cref="Imaging.ModelCatalog.DefaultModelsFolder"/>.</param>
    /// <param name="logger">Receives download events.</param>
    public ModelDownloader(HttpClient http, string modelsFolder, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        _http = http;
        _modelsFolder = Path.GetFullPath(modelsFolder);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the models folder.</summary>
    public string ModelsFolder => _modelsFolder;

    /// <summary>
    /// Downloads, verifies and installs a model, resuming an earlier partial download when there is one.
    /// </summary>
    /// <param name="model">The model from the manifest.</param>
    /// <param name="progress">Receives progress, at most about ten times a second plus a final report.</param>
    /// <param name="pause">Pauses the transfer between buffer chunks.</param>
    /// <param name="cancellationToken">Cancels the download; the partial file is kept for a later resume.</param>
    /// <returns>The installed model.</returns>
    /// <exception cref="ModelSizeMismatchException">The downloaded size differs from the manifest; the partial was deleted.</exception>
    /// <exception cref="ModelHashMismatchException">The SHA-256 differs from the manifest; the partial was deleted.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached or answered with an error; the partial is kept.</exception>
    public async Task<InstalledModel> DownloadAsync(ManifestModel model, IProgress<DownloadProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!ModelManifest.IsSafeFileName(model.FileName) || model.Size <= 0 || model.Url is null || string.IsNullOrEmpty(model.Sha256))
        {
            throw new ArgumentException($"Model '{model.Name}' is not a valid manifest entry.", nameof(model));
        }

        Directory.CreateDirectory(_modelsFolder);
        var finalPath = Path.Combine(_modelsFolder, model.FileName);
        var partialPath = finalPath + PartialSuffix;

        // The licence comes first so an installed model is never without it.
        var licencePath = await EnsureLicenceAsync(model, cancellationToken).ConfigureAwait(false);

        var (sha256, resumed) = await TransferAsync(model, partialPath, progress, pause, cancellationToken).ConfigureAwait(false);

        File.Move(partialPath, finalPath, overwrite: true);
        var sidecarPath = await WriteSidecarAsync(model, finalPath, sha256, cancellationToken).ConfigureAwait(false);
        LogInstalled(_logger, model.Name, finalPath, model.Size);
        return new InstalledModel(model.Name, finalPath, sidecarPath, licencePath, model.Size, sha256, resumed);
    }

    private async Task<(string Sha256, bool Resumed)> TransferAsync(
        ManifestModel model,
        string partialPath,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > model.Size)
        {
            LogPartialTooLarge(_logger, model.Name, existing, model.Size);
            File.Delete(partialPath);
            existing = 0;
        }

        if (existing > 0)
        {
            await HashExistingAsync(partialPath, hash, pause, cancellationToken).ConfigureAwait(false);
            LogResuming(_logger, model.Name, existing, model.Size);
        }

        var resumed = existing > 0;
        if (existing < model.Size)
        {
            existing = await DownloadRemainderAsync(model, partialPath, existing, hash, progress, pause, cancellationToken).ConfigureAwait(false);
            resumed &= existing > 0;
        }
        else
        {
            progress?.Report(new DownloadProgress(existing, model.Size, 0));
        }

        var actualSize = new FileInfo(partialPath).Length;
        if (actualSize != model.Size)
        {
            File.Delete(partialPath);
            LogVerificationFailed(_logger, model.Name, "size", model.Size.ToString(CultureInfo.InvariantCulture), actualSize.ToString(CultureInfo.InvariantCulture));
            throw new ModelSizeMismatchException($"Model '{model.Name}' downloaded {actualSize} bytes; the manifest says {model.Size}. The partial file was deleted.")
            {
                ModelName = model.Name,
                Expected = model.Size.ToString(CultureInfo.InvariantCulture),
                Actual = actualSize.ToString(CultureInfo.InvariantCulture),
            };
        }

        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partialPath);
            LogVerificationFailed(_logger, model.Name, "sha256", model.Sha256, actualHash);
            throw new ModelHashMismatchException($"Model '{model.Name}' has SHA-256 {actualHash}; the manifest says {model.Sha256}. The partial file was deleted.")
            {
                ModelName = model.Name,
                Expected = model.Sha256,
                Actual = actualHash,
            };
        }

        return (actualHash, resumed);
    }

    /// <summary>Downloads from <paramref name="offset"/> to the end; returns the offset the transfer actually started from.</summary>
    private async Task<long> DownloadRemainderAsync(
        ManifestModel model,
        string partialPath,
        long offset,
        IncrementalHash hash,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendAsync(model.Url, offset, cancellationToken).ConfigureAwait(false);
        try
        {
            if (offset > 0 && !IsValidResume(response, offset))
            {
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // A server error is not a refused range: keep the partial for the next attempt.
                    response.EnsureSuccessStatusCode();
                }

                LogRangeIgnored(_logger, model.Name, (int)response.StatusCode);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    // 416 or a 206 for the wrong range: ask again for the whole file.
                    response.Dispose();
                    response = await SendAsync(model.Url, 0, cancellationToken).ConfigureAwait(false);
                }

                offset = 0;
                hash.GetHashAndReset();
            }

            response.EnsureSuccessStatusCode();

            var file = new FileStream(partialPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await CopyAsync(model, body, file, offset, hash, progress, pause, cancellationToken).ConfigureAwait(false);
                }
            }

            return offset;
        }
        finally
        {
            response.Dispose();
        }
    }

    private static bool IsValidResume(HttpResponseMessage response, long offset) =>
        response.StatusCode == HttpStatusCode.PartialContent
        && response.Content.Headers.ContentRange is { From: { } from } && from == offset;

    private async Task<HttpResponseMessage> SendAsync(Uri url, long offset, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyAsync(
        ManifestModel model,
        Stream body,
        FileStream file,
        long offset,
        IncrementalHash hash,
        IProgress<DownloadProgress>? progress,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var done = offset;
            long transferred = 0;
            var active = Stopwatch.StartNew();
            var lastReport = -_progressInterval;
            progress?.Report(new DownloadProgress(done, model.Size, 0));

            while (true)
            {
                if (pause.IsPaused)
                {
                    active.Stop();
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    LogPaused(_logger, model.Name, done);
                    await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    active.Start();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (done + read > model.Size)
                {
                    // More bytes than the manifest promises: stop early, verification reports the mismatch.
                    read = (int)(model.Size - done) + 1;
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                transferred += read;

                if (progress is not null && (active.Elapsed - lastReport >= _progressInterval || done == model.Size))
                {
                    lastReport = active.Elapsed;
                    progress.Report(new DownloadProgress(done, model.Size, Rate(transferred, active.Elapsed)));
                }
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(done, model.Size, Rate(transferred, active.Elapsed)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static double Rate(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds;

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

    private async Task<string?> EnsureLicenceAsync(ManifestModel model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.LicenceFile) || model.LicenceUrl is null)
        {
            return null;
        }

        if (!ModelManifest.IsSafeFileName(model.LicenceFile))
        {
            throw new ArgumentException($"Model '{model.Name}' has an invalid licence file name.", nameof(model));
        }

        var path = Path.Combine(_modelsFolder, model.LicenceFile);
        if (File.Exists(path))
        {
            return path;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.LicenceUrl);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        LogLicenceDownloaded(_logger, model.LicenceFile, path);
        return path;
    }

    private static async Task<string> WriteSidecarAsync(ManifestModel model, string modelPath, string sha256, CancellationToken cancellationToken)
    {
        var sidecarPath = Path.ChangeExtension(modelPath, ".json");
        var sidecar = new InstalledModelSidecar(
            model.DisplayName,
            model.Scale,
            model.Precision?.Trim().ToLowerInvariant(),
            model.Licence,
            model.RecommendedFor,
            sha256,
            model.Size,
            model.Tier,
            model.Family);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, ModelsJsonContext.Default.InstalledModelSidecar);
        await WriteAtomicAsync(sidecarPath, bytes, cancellationToken).ConfigureAwait(false);
        return sidecarPath;
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
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Resuming {Model} at {Offset} of {Total} bytes")]
    private static partial void LogResuming(ILogger logger, string model, long offset, long total);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Partial download of {Model} is {Length} bytes, larger than {Total}; restarting")]
    private static partial void LogPartialTooLarge(ILogger logger, string model, long length, long total);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Server did not honour the range for {Model} (HTTP {StatusCode}); restarting from zero")]
    private static partial void LogRangeIgnored(ILogger logger, string model, int statusCode);

    [LoggerMessage(EventId = 203, Level = LogLevel.Information, Message = "Download of {Model} paused at {Bytes} bytes")]
    private static partial void LogPaused(ILogger logger, string model, long bytes);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Verification of {Model} failed: {Check} expected {Expected}, got {Actual}")]
    private static partial void LogVerificationFailed(ILogger logger, string model, string check, string expected, string actual);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information, Message = "Installed {Model} at {Path} ({Size} bytes)")]
    private static partial void LogInstalled(ILogger logger, string model, string path, long size);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "Downloaded licence {LicenceFile} to {Path}")]
    private static partial void LogLicenceDownloaded(ILogger logger, string licenceFile, string path);
}
