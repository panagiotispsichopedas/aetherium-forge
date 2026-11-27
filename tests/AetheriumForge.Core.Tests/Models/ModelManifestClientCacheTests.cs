using System.Net;
using System.Net.Http.Headers;
using AetheriumForge.Core.Models;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelManifestClientCacheTests : IDisposable
{
    private const string Sha = "e1e123120ab5c6e31ccb2bea951a3c2c815b5a6c88e316d6f0aa7370def26abb";
    private const string ReleasesPath = "/repos/o/r/releases";

    private readonly TempModelsFolder _folder = new();
    private readonly ManualTimeProvider _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private ModelSourceOptions Options()
    {
        var options = ModelTestData.Options(_folder.ModelsFolder);
        options.TimeProvider = _clock;
        return options;
    }

    private ModelManifestClient Client(FakeHttpHandler handler) => new(new HttpClient(handler), Options());

    private static string Releases(params string[] tags) =>
        $"[{string.Join(',', tags.Select(tag => $$"""{ "tag_name": "{{tag}}", "draft": false, "assets": [] }"""))}]";

    private static HttpResponseMessage ManifestFor(string release) => ModelTestData.Json(ModelTestData.ManifestJson(release, ("m", Sha, 10)));

    private static bool IsManifest(HttpRequestMessage request, string tag) => request.RequestUri!.AbsolutePath == $"/o/r/releases/download/{tag}/models.json";

    private static HttpResponseMessage ListingWithETag(string json, string etag)
    {
        var response = ModelTestData.Json(json);
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private static HttpResponseMessage RateLimited()
    {
        var response = ModelTestData.Status(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        return response;
    }

    private static FakeHttpHandler Standard(string tag) => new(request => request.RequestUri!.AbsolutePath == ReleasesPath
        ? ListingWithETag(Releases(tag), "\"r1\"")
        : IsManifest(request, tag) ? ManifestFor(tag) : ModelTestData.Status(HttpStatusCode.NotFound));

    [Fact]
    public async Task GetLatestAsync_WithinDiscoveryWindow_SkipsReleaseList()
    {
        // Arrange
        var handler = Standard("models-v3");
        await Client(handler).GetLatestAsync(Ct);
        _clock.Now += TimeSpan.FromMinutes(9);

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v3", 1), (manifest.Release, handler.Requests.Count(request => request.RequestUri!.AbsolutePath == ReleasesPath)));
    }

    [Fact]
    public async Task GetLatestAsync_AfterDiscoveryWindow_RevalidatesReleaseListWithETag()
    {
        // Arrange
        await Client(Standard("models-v3")).GetLatestAsync(Ct);
        _clock.Now += TimeSpan.FromMinutes(11);
        var second = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"r1\"") ? ModelTestData.Status(HttpStatusCode.NotModified) : ModelTestData.Status(HttpStatusCode.InternalServerError)
            : IsManifest(request, "models-v3") ? ManifestFor("models-v3") : ModelTestData.Status(HttpStatusCode.NotFound));

        // Act
        var manifest = await Client(second).GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v3", "\"r1\""), (manifest.Release, second.Requests[0].Headers.IfNoneMatch.Single().Tag));
    }

    [Fact]
    public async Task GetLatestAsync_ModelsTagOnSecondPage_FollowsLinkNext()
    {
        // Arrange
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == ReleasesPath)
            {
                if (request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal))
                {
                    return ModelTestData.Json(Releases("models-v7"));
                }

                var first = ModelTestData.Json(Releases("v3.0.0", "v2.0.0"));
                first.Headers.Add("Link", "<https://api.github.com/repos/o/r/releases?per_page=100&page=2>; rel=\"next\", <https://api.github.com/repos/o/r/releases?per_page=100&page=2>; rel=\"last\"");
                return first;
            }

            return IsManifest(request, "models-v7") ? ManifestFor("models-v7") : ModelTestData.Status(HttpStatusCode.NotFound);
        });

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v7", manifest.Release);
    }

    [Fact]
    public async Task GetLatestAsync_NextLinkToOtherHost_StopsPagingAndFallsBack()
    {
        // Arrange
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == ReleasesPath)
            {
                var first = ModelTestData.Json(Releases("v3.0.0"));
                first.Headers.Add("Link", "<https://evil.example/repos/o/r/releases?page=2>; rel=\"next\"");
                return first;
            }

            return IsManifest(request, "models-v1") ? ManifestFor("models-v1") : ModelTestData.Status(HttpStatusCode.NotFound);
        });

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v1", false), (manifest.Release, handler.RequestedUrls.Any(url => url.Host == "evil.example")));
    }

    [Fact]
    public async Task GetLatestAsync_RateLimitedAfterEarlierDiscovery_UsesLastDiscoveredTagFirst()
    {
        // Arrange
        await Client(Standard("models-v3")).GetLatestAsync(Ct);
        _clock.Now += TimeSpan.FromMinutes(11);
        var limited = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? RateLimited()
            : IsManifest(request, "models-v3") ? ManifestFor("models-v3") : ManifestFor("models-v1"));

        // Act
        var manifest = await Client(limited).GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v3", manifest.Release);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetLatestAsync_ResiliencePipelineRejects_FallsBackToDirectUrl(bool brokenCircuit)
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? throw (brokenCircuit ? new BrokenCircuitException() : new TimeoutRejectedException())
            : IsManifest(request, "models-v1") ? ManifestFor("models-v1") : ModelTestData.Status(HttpStatusCode.NotFound));

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v1", manifest.Release);
    }

    [Fact]
    public async Task GetLatestAsync_DiscoveredManifestUnsupported_TriesFallbackTag()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases("models-v5"))
            : IsManifest(request, "models-v5")
                ? ModelTestData.Json("""{ "schemaVersion": 2, "release": "models-v5", "models": [] }""")
                : ManifestFor("models-v1"));

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v1", manifest.Release);
    }

    [Fact]
    public async Task GetLatestAsync_AllManifestsInvalidButCached_ReturnsCache()
    {
        // Arrange
        await Client(Standard("models-v3")).GetLatestAsync(Ct);
        _clock.Now += TimeSpan.FromMinutes(11);
        var broken = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases("models-v4"))
            : ModelTestData.Json("""{ "schemaVersion": 2, "release": "x", "models": [] }"""));

        // Act
        var manifest = await Client(broken).GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v3", ManifestOrigin.Cache), (manifest.Release, manifest.Origin));
    }

    [Fact]
    public async Task GetLatestAsync_ManifestRedirectedToUntrustedHost_TriesNextSource()
    {
        // Arrange
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == ReleasesPath)
            {
                return ModelTestData.Json(Releases("models-v2"));
            }

            if (IsManifest(request, "models-v2"))
            {
                var redirected = ManifestFor("models-v2");
                redirected.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/models.json");
                return redirected;
            }

            return ManifestFor("models-v1");
        });

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v1", manifest.Release);
    }

    [Fact]
    public async Task GetLatestAsync_AssetUrlOutsideRepository_UsesConventionalUrl()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ReleasesPath => ModelTestData.Json("""[{ "tag_name": "models-v6", "draft": false, "assets": [{ "name": "models.json", "browser_download_url": "https://evil.example/models.json" }] }]"""),
            "/o/r/releases/download/models-v6/models.json" => ManifestFor("models-v6"),
            _ => ModelTestData.Status(HttpStatusCode.NotFound),
        });

        // Act
        var manifest = await Client(handler).GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v6", false), (manifest.Release, handler.RequestedUrls.Any(url => url.Host == "evil.example")));
    }

    [Fact]
    public async Task GetLatestAsync_RepeatedCalls_LeaveNoTemporaryCacheFiles()
    {
        // Arrange
        var handler = Standard("models-v3");

        // Act
        await Task.WhenAll(Client(handler).GetLatestAsync(Ct), Client(handler).GetLatestAsync(Ct));

        // Assert
        Assert.Empty(Directory.GetFiles(_folder.Root, "*.tmp"));
    }
}
