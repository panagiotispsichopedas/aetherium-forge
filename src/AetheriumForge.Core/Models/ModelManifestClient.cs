using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Finds and downloads the newest <c>models.json</c> (IMPLEMENTATION_GUIDE §9, discovery contract).
/// </summary>
/// <remarks>
/// Lists the repository releases through the GitHub API, keeps non-draft releases tagged <c>models-vN</c> and takes the highest N; it never
/// uses <c>/releases/latest</c>. When the API is rate-limited or unreachable it downloads the manifest of
/// <see cref="ModelSourceOptions.FallbackTag"/> directly, and when that fails too it returns the cached copy. The manifest is cached
/// with its ETag and revalidated with <c>If-None-Match</c>.
/// </remarks>
public sealed partial class ModelManifestClient
{
    private static readonly MediaTypeWithQualityHeaderValue _gitHubJson = new("application/vnd.github+json");

    private readonly HttpClient _http;
    private readonly ModelSourceOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelManifestClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client; register it with a resilience handler (see <see cref="ModelDownloadsServiceCollectionExtensions.AddModelDownloads"/>).</param>
    /// <param name="options">The source options.</param>
    /// <param name="logger">Receives discovery and fallback events.</param>
    public ModelManifestClient(HttpClient http, ModelSourceOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets the manifest of the newest <c>models-vN</c> release.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The manifest; <see cref="ModelManifest.Origin"/> says whether it is fresh, revalidated or cached.</returns>
    /// <exception cref="ModelSourceUnavailableException">No source answered and there is no usable cache.</exception>
    /// <exception cref="ModelManifestException">A downloaded manifest is invalid or uses an unsupported schema.</exception>
    public async Task<ModelManifest> GetLatestAsync(CancellationToken cancellationToken)
    {
        var cache = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        var fallback = _options.ReleaseAssetUrl(_options.FallbackTag, ModelSourceOptions.ManifestAssetName);

        var candidates = new List<Uri>(2);
        var discovered = await TryDiscoverManifestUrlAsync(cancellationToken).ConfigureAwait(false);
        if (discovered is not null)
        {
            candidates.Add(discovered);
        }

        if (!candidates.Contains(fallback))
        {
            candidates.Add(fallback);
        }

        Exception? lastError = null;
        foreach (var url in candidates)
        {
            try
            {
                return await DownloadManifestAsync(url, cache, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                LogManifestDownloadFailed(_logger, exception, url);
                lastError = exception;
            }
        }

        if (cache is not null)
        {
            LogUsingCache(_logger, cache.Url, cache.FetchedAt);
            return ParseCached(cache) with { Origin = ManifestOrigin.Cache };
        }

        throw lastError is null
            ? new ModelSourceUnavailableException()
            : new ModelSourceUnavailableException("The model list is unavailable: GitHub could not be reached and there is no cached copy.", lastError);
    }

    /// <summary>
    /// Picks the highest <c>models-vN</c> release among non-draft releases.
    /// </summary>
    /// <param name="releases">The releases as listed by the GitHub API.</param>
    /// <returns>The release, or <see langword="null"/> when none matches.</returns>
    internal static GitHubRelease? SelectLatest(IEnumerable<GitHubRelease?> releases)
    {
        GitHubRelease? best = null;
        var bestNumber = -1L;
        foreach (var release in releases)
        {
            if (release is null || release.Draft || release.TagName is null)
            {
                continue;
            }

            var match = ModelsTag().Match(release.TagName);
            if (!match.Success || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (number > bestNumber)
            {
                best = release;
                bestNumber = number;
            }
        }

        return best;
    }

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
        && response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
        && values.Any(value => value.Trim() == "0");

    private async Task<Uri?> TryDiscoverManifestUrlAsync(CancellationToken cancellationToken)
    {
        var releasesUrl = _options.ReleasesUrl();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            request.Headers.Accept.Add(_gitHubJson);
            request.Headers.UserAgent.Add(_options.UserAgentHeader());
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRateLimited(response))
            {
                LogRateLimited(_logger, (int)response.StatusCode);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogReleaseListFailed(_logger, (int)response.StatusCode);
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var releases = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.IReadOnlyListGitHubRelease, cancellationToken).ConfigureAwait(false) ?? [];
                var latest = SelectLatest(releases);
                if (latest is null)
                {
                    LogNoModelsRelease(_logger);
                    return null;
                }

                var asset = latest.Assets?.FirstOrDefault(candidate => string.Equals(candidate.Name, ModelSourceOptions.ManifestAssetName, StringComparison.Ordinal));
                var url = asset?.BrowserDownloadUrl is { IsAbsoluteUri: true } assetUrl && assetUrl.Scheme == Uri.UriSchemeHttps
                    ? assetUrl
                    : _options.ReleaseAssetUrl(latest.TagName!, ModelSourceOptions.ManifestAssetName);
                LogDiscovered(_logger, latest.TagName!, url);
                return url;
            }
        }
        catch (JsonException exception)
        {
            LogReleaseListUnreadable(_logger, exception);
            return null;
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            LogReleaseListUnreachable(_logger, exception);
            return null;
        }
    }

    private async Task<ModelManifest> DownloadManifestAsync(Uri url, ManifestCacheEntry? cache, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.Add(_options.UserAgentHeader());
        var conditional = cache is not null && cache.Url == url && !string.IsNullOrEmpty(cache.ETag);
        if (conditional && EntityTagHeaderValue.TryParse(cache!.ETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && conditional)
        {
            LogNotModified(_logger, url);
            return ParseCached(cache!) with { Origin = ManifestOrigin.NotModified };
        }

        if (IsRateLimited(response))
        {
            LogRateLimited(_logger, (int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var manifest = ModelManifest.Parse(bytes);

        var newEtag = response.Headers.ETag?.ToString();
        await WriteCacheAsync(new ManifestCacheEntry(url, newEtag, DateTimeOffset.UtcNow, Encoding.UTF8.GetString(bytes)), cancellationToken).ConfigureAwait(false);
        LogManifestDownloaded(_logger, manifest.Release, manifest.Models.Count, url);
        return manifest;
    }

    private static ModelManifest ParseCached(ManifestCacheEntry cache) => ModelManifest.Parse(Encoding.UTF8.GetBytes(cache.Content));

    private async Task<ManifestCacheEntry?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        var path = _options.ResolveCachePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                var entry = await JsonSerializer.DeserializeAsync(stream, ModelsJsonContext.Default.ManifestCacheEntry, cancellationToken).ConfigureAwait(false);
                if (entry?.Url is null || string.IsNullOrEmpty(entry.Content))
                {
                    LogCacheUnreadable(_logger, null, path);
                    return null;
                }

                // A cached manifest that no longer parses (for example after a schema change) is not usable as a fallback.
                ParseCached(entry);
                return entry;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ModelManifestException)
        {
            LogCacheUnreadable(_logger, exception, path);
            return null;
        }
    }

    private async Task WriteCacheAsync(ManifestCacheEntry entry, CancellationToken cancellationToken)
    {
        var path = _options.ResolveCachePath();
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var stream = File.Create(temp);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, entry, ModelsJsonContext.Default.ManifestCacheEntry, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The manifest is still returned; only the offline fallback is lost.
            LogCacheWriteFailed(_logger, exception, path);
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [GeneratedRegex(@"^models-v(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelsTag();

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Discovered model release {Tag}; manifest at {Url}")]
    private static partial void LogDiscovered(ILogger logger, string tag, Uri url);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "GitHub API rate limit reached (HTTP {StatusCode}); using the direct manifest URL")]
    private static partial void LogRateLimited(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Listing releases failed with HTTP {StatusCode}; using the direct manifest URL")]
    private static partial void LogReleaseListFailed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "GitHub API unreachable; using the direct manifest URL")]
    private static partial void LogReleaseListUnreachable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "The release list could not be read; using the direct manifest URL")]
    private static partial void LogReleaseListUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "No models-vN release found; using the direct manifest URL")]
    private static partial void LogNoModelsRelease(ILogger logger);

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning, Message = "Downloading the manifest from {Url} failed")]
    private static partial void LogManifestDownloadFailed(ILogger logger, Exception exception, Uri url);

    [LoggerMessage(EventId = 107, Level = LogLevel.Warning, Message = "Offline: using the manifest cached from {Url} at {FetchedAt}")]
    private static partial void LogUsingCache(ILogger logger, Uri url, DateTimeOffset fetchedAt);

    [LoggerMessage(EventId = 108, Level = LogLevel.Information, Message = "Manifest at {Url} not modified; using the cached copy")]
    private static partial void LogNotModified(ILogger logger, Uri url);

    [LoggerMessage(EventId = 109, Level = LogLevel.Information, Message = "Downloaded manifest {Release} with {Count} models from {Url}")]
    private static partial void LogManifestDownloaded(ILogger logger, string release, int count, Uri url);

    [LoggerMessage(EventId = 110, Level = LogLevel.Warning, Message = "Ignoring unreadable manifest cache {Path}")]
    private static partial void LogCacheUnreadable(ILogger logger, Exception? exception, string path);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Could not write the manifest cache {Path}")]
    private static partial void LogCacheWriteFailed(ILogger logger, Exception exception, string path);
}
