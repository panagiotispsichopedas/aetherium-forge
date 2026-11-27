using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Finds and downloads the newest <c>models.json</c> (IMPLEMENTATION_GUIDE §9, discovery contract).
/// </summary>
/// <remarks>
/// <para>
/// Lists the repository releases through the GitHub API (following <c>Link: rel="next"</c> pages until a <c>models-vN</c> tag appears),
/// keeps non-draft releases tagged <c>models-vN</c> and takes the highest N; it never uses <c>/releases/latest</c>. The discovery result
/// is reused for <see cref="ModelSourceOptions.DiscoveryCacheDuration"/> and then revalidated with the release list's ETag, so a
/// <c>304</c> does not count against the rate limit.
/// </para>
/// <para>
/// When the API is rate-limited or unreachable the manifest of the last discovered tag is downloaded directly, then that of
/// <see cref="ModelSourceOptions.FallbackTag"/>, and when all fail (network error or an invalid manifest) the cached copy is returned.
/// The manifest is cached with its ETag and revalidated with <c>If-None-Match</c>.
/// </para>
/// <para>
/// <b>Trust boundary.</b> The manifest's SHA-256 values come from the same release as the models, so integrity rests on write access to
/// the repository's releases; there is no separate signature. Manifest and model URLs must be release assets of the configured repository.
/// </para>
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
    /// <exception cref="ModelManifestException">Every downloaded manifest was invalid (for example an unsupported schema) and there is no usable cache.</exception>
    public async Task<ModelManifest> GetLatestAsync(CancellationToken cancellationToken)
    {
        var cache = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        var cachedManifest = TryParseCached(cache);
        var discovery = await DiscoverAsync(cache, cancellationToken).ConfigureAwait(false);

        var candidates = new List<Uri>(3);
        AddCandidate(candidates, discovery?.Url);
        if (cache?.DiscoveredTag is { } lastTag && ModelsTag().IsMatch(lastTag))
        {
            AddCandidate(candidates, _options.ReleaseAssetUrl(lastTag, ModelSourceOptions.ManifestAssetName));
        }

        AddCandidate(candidates, _options.ReleaseAssetUrl(_options.FallbackTag, ModelSourceOptions.ManifestAssetName));

        Exception? lastNetworkError = null;
        ModelManifestException? lastManifestError = null;
        foreach (var url in candidates)
        {
            try
            {
                return await DownloadManifestAsync(url, cache, cachedManifest, discovery, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelManifestException exception)
            {
                LogManifestInvalid(_logger, exception, url);
                lastManifestError = exception;
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
            {
                LogManifestDownloadFailed(_logger, exception, url);
                lastNetworkError = exception;
            }
        }

        if (cachedManifest is not null)
        {
            LogUsingCache(_logger, cache!.Url!, cache.FetchedAt);
            return cachedManifest with { Origin = ManifestOrigin.Cache };
        }

        if (lastManifestError is not null)
        {
            throw lastManifestError;
        }

        throw lastNetworkError is null
            ? new ModelSourceUnavailableException()
            : new ModelSourceUnavailableException("The model list is unavailable: GitHub could not be reached and there is no cached copy.", lastNetworkError);
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

    /// <summary>Reads the <c>rel="next"</c> target of a GitHub <c>Link</c> header.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The next page URL, or <see langword="null"/>.</returns>
    internal static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in values.SelectMany(value => value.Split(',')))
        {
            var start = part.IndexOf('<', StringComparison.Ordinal);
            var end = part.IndexOf('>', StringComparison.Ordinal);
            if (start >= 0 && end > start && part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(part[(start + 1)..end], UriKind.Absolute, out var next))
            {
                return next;
            }
        }

        return null;
    }

    private static void AddCandidate(List<Uri> candidates, Uri? url)
    {
        if (url is not null && !candidates.Contains(url))
        {
            candidates.Add(url);
        }
    }

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or BrokenCircuitException or TimeoutRejectedException or UntrustedModelSourceException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
        && response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
        && values.Any(value => value.Trim() == "0");

    private ModelManifest? TryParseCached(ManifestCacheEntry? cache)
    {
        if (cache?.Url is null || string.IsNullOrEmpty(cache.Content))
        {
            return null;
        }

        try
        {
            return ModelManifest.Parse(Encoding.UTF8.GetBytes(cache.Content), _options);
        }
        catch (ModelManifestException exception)
        {
            // A cached manifest that no longer parses (for example after a schema change) is not usable as a fallback.
            LogCacheUnreadable(_logger, exception, _options.ResolveCachePath());
            return null;
        }
    }

    private async Task<Discovery?> DiscoverAsync(ManifestCacheEntry? cache, CancellationToken cancellationToken)
    {
        var now = _options.TimeProvider.GetUtcNow();
        if (cache?.DiscoveredUrl is { } cachedUrl && cache.DiscoveredTag is { } cachedTag && cache.DiscoveredAt is { } at
            && at <= now && now - at < _options.DiscoveryCacheDuration)
        {
            LogDiscoveryCached(_logger, cachedTag, at);
            return new Discovery(cachedUrl, cachedTag, cache.ReleasesETag, at);
        }

        var pageUrl = _options.ReleasesUrl();
        string? releasesETag = null;
        try
        {
            for (var page = 1; page <= Math.Max(1, _options.MaxReleasePages); page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                request.Headers.Accept.Add(_gitHubJson);
                request.Headers.UserAgent.Add(_options.UserAgentHeader());
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                var revalidate = page == 1 && cache?.DiscoveredUrl is not null && cache.DiscoveredTag is not null
                    && EntityTagHeaderValue.TryParse(cache.ReleasesETag, out _);
                if (revalidate)
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cache!.ReleasesETag!));
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (revalidate && response.StatusCode == HttpStatusCode.NotModified)
                {
                    LogReleasesNotModified(_logger, cache!.DiscoveredTag!);
                    return new Discovery(cache.DiscoveredUrl!, cache.DiscoveredTag!, cache.ReleasesETag, now);
                }

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

                if (page == 1)
                {
                    releasesETag = response.Headers.ETag?.ToString();
                }

                IReadOnlyList<GitHubRelease> releases;
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    releases = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.IReadOnlyListGitHubRelease, cancellationToken).ConfigureAwait(false) ?? [];
                }

                var latest = SelectLatest(releases);
                if (latest is not null)
                {
                    var asset = latest.Assets?.FirstOrDefault(candidate => string.Equals(candidate.Name, ModelSourceOptions.ManifestAssetName, StringComparison.Ordinal));
                    var url = asset?.BrowserDownloadUrl is { } assetUrl && _options.IsReleaseAssetUrl(assetUrl)
                        ? assetUrl
                        : _options.ReleaseAssetUrl(latest.TagName!, ModelSourceOptions.ManifestAssetName);
                    LogDiscovered(_logger, latest.TagName!, url, page);
                    return new Discovery(url, latest.TagName!, releasesETag, now);
                }

                var next = NextPage(response);
                if (next is null || !string.Equals(next.Host, _options.ApiBaseAddress.Host, StringComparison.OrdinalIgnoreCase) || next.Scheme != Uri.UriSchemeHttps)
                {
                    break;
                }

                pageUrl = next;
            }

            LogNoModelsRelease(_logger);
            return null;
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

    private async Task<ModelManifest> DownloadManifestAsync(Uri url, ManifestCacheEntry? cache, ModelManifest? cachedManifest, Discovery? discovery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.Add(_options.UserAgentHeader());
        var conditional = cachedManifest is not null && cache!.Url == url && EntityTagHeaderValue.TryParse(cache.ETag, out _);
        if (conditional)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cache!.ETag!));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var final = response.RequestMessage?.RequestUri ?? url;
        if (!_options.IsTrustedDownloadUrl(final))
        {
            throw new UntrustedModelSourceException($"The manifest download {url} ended on untrusted host '{final.Host}'.") { Url = final };
        }

        if (conditional && response.StatusCode == HttpStatusCode.NotModified)
        {
            LogNotModified(_logger, url);
            await WriteCacheAsync(Merge(cache!, discovery) with { FetchedAt = _options.TimeProvider.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
            return cachedManifest! with { Origin = ManifestOrigin.NotModified };
        }

        if (IsRateLimited(response))
        {
            LogRateLimited(_logger, (int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var manifest = ModelManifest.Parse(bytes, _options);

        var entry = Merge(cache ?? new ManifestCacheEntry(), discovery) with
        {
            Url = url,
            ETag = response.Headers.ETag?.ToString(),
            FetchedAt = _options.TimeProvider.GetUtcNow(),
            Content = Encoding.UTF8.GetString(bytes),
        };
        await WriteCacheAsync(entry, cancellationToken).ConfigureAwait(false);
        LogManifestDownloaded(_logger, manifest.Release, manifest.Models.Count, url);
        return manifest;
    }

    private static ManifestCacheEntry Merge(ManifestCacheEntry cache, Discovery? discovery) => discovery is null
        ? cache
        : cache with { DiscoveredUrl = discovery.Url, DiscoveredTag = discovery.Tag, ReleasesETag = discovery.ReleasesETag, DiscoveredAt = discovery.At };

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
                return await JsonSerializer.DeserializeAsync(stream, ModelsJsonContext.Default.ManifestCacheEntry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LogCacheUnreadable(_logger, exception, path);
            return null;
        }
    }

    private async Task WriteCacheAsync(ManifestCacheEntry entry, CancellationToken cancellationToken)
    {
        var path = _options.ResolveCachePath();
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, entry, ModelsJsonContext.Default.ManifestCacheEntry, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            created = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The manifest is still returned; only the offline fallback is lost.
            LogCacheWriteFailed(_logger, exception, path);
        }
        finally
        {
            if (created && File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [GeneratedRegex(@"^models-v(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelsTag();

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Discovered model release {Tag} on page {Page}; manifest at {Url}")]
    private static partial void LogDiscovered(ILogger logger, string tag, Uri url, int page);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "GitHub API rate limit reached (HTTP {StatusCode}); using a direct manifest URL")]
    private static partial void LogRateLimited(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Listing releases failed with HTTP {StatusCode}; using a direct manifest URL")]
    private static partial void LogReleaseListFailed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "GitHub API unreachable; using a direct manifest URL")]
    private static partial void LogReleaseListUnreachable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "The release list could not be read; using a direct manifest URL")]
    private static partial void LogReleaseListUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "No models-vN release found; using a direct manifest URL")]
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

    [LoggerMessage(EventId = 112, Level = LogLevel.Warning, Message = "The manifest at {Url} is invalid; trying the next source")]
    private static partial void LogManifestInvalid(ILogger logger, Exception exception, Uri url);

    [LoggerMessage(EventId = 113, Level = LogLevel.Debug, Message = "Using release {Tag} discovered at {At}")]
    private static partial void LogDiscoveryCached(ILogger logger, string tag, DateTimeOffset at);

    [LoggerMessage(EventId = 114, Level = LogLevel.Information, Message = "Release list not modified; still {Tag}")]
    private static partial void LogReleasesNotModified(ILogger logger, string tag);

    /// <summary>A discovery result.</summary>
    /// <param name="Url">The manifest URL.</param>
    /// <param name="Tag">The release tag.</param>
    /// <param name="ReleasesETag">The first release-list page's ETag.</param>
    /// <param name="At">When the release list was read or revalidated.</param>
    private sealed record Discovery(Uri Url, string Tag, string? ReleasesETag, DateTimeOffset At);
}
