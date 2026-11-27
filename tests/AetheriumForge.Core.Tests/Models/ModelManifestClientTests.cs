using System.Net;
using AetheriumForge.Core.Models;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelManifestClientTests : IDisposable
{
    private const string Sha = "e1e123120ab5c6e31ccb2bea951a3c2c815b5a6c88e316d6f0aa7370def26abb";
    private const string ReleasesPath = "/repos/o/r/releases";

    private readonly TempModelsFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private ModelSourceOptions Options() => new()
    {
        Owner = "o",
        Repo = "r",
        ModelsFolder = _folder.ModelsFolder,
    };

    private static string Release(string tag, bool draft = false, bool withManifestAsset = true) =>
        $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": false, "name": "x",
          "assets": [{{(withManifestAsset ? $$"""{ "name": "models.json", "browser_download_url": "https://github.com/o/r/releases/download/{{tag}}/models.json" }""" : string.Empty)}}]
        }
        """;

    private static string Releases(params string[] releases) => $"[{string.Join(',', releases)}]";

    private static HttpResponseMessage ManifestFor(string release) => ModelTestData.Json(ModelTestData.ManifestJson(release, ("m", Sha, 10)));

    private static HttpResponseMessage RateLimited(HttpStatusCode status)
    {
        var response = ModelTestData.Json("""{ "message": "API rate limit exceeded" }""", status);
        response.Headers.Add("x-ratelimit-remaining", "0");
        return response;
    }

    [Fact]
    public async Task GetLatestAsync_SeveralReleases_PicksHighestNonDraftModelsTag()
    {
        // Arrange: v10 beats v9 numerically, the v11 draft, app tags and look-alike tags are ignored.
        var releases = Releases(
            Release("v2.0.0"),
            Release("models-v9"),
            Release("models-v11", draft: true),
            Release("models-v10"),
            Release("models-v12-beta"),
            Release("xmodels-v99"));
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ReleasesPath => ModelTestData.Json(releases),
            "/o/r/releases/download/models-v10/models.json" => ManifestFor("models-v10"),
            _ => ModelTestData.Status(HttpStatusCode.NotFound),
        });
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        var manifest = await client.GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v10", manifest.Release);
    }

    [Fact]
    public async Task GetLatestAsync_Always_NeverRequestsReleasesLatest()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ReleasesPath => ModelTestData.Json(Releases(Release("models-v1"))),
            _ => ManifestFor("models-v1"),
        });
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        await client.GetLatestAsync(Ct);

        // Assert
        Assert.DoesNotContain(handler.RequestedUrls, url => url.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLatestAsync_ListingRequest_SendsGitHubHeadersWithoutToken()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases(Release("models-v1")))
            : ManifestFor("models-v1"));
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        await client.GetLatestAsync(Ct);

        // Assert
        var listing = handler.Requests.First();
        Assert.Equal(
            ("https://api.github.com/repos/o/r/releases?per_page=100", "application/vnd.github+json", true, false),
            (listing.RequestUri!.AbsoluteUri, listing.Headers.Accept.Single().MediaType, listing.Headers.UserAgent.ToString().StartsWith("AetheriumForge/", StringComparison.Ordinal), listing.Headers.Authorization is not null));
    }

    [Fact]
    public void SelectLatest_OnlyDraftsAndOtherTags_ReturnsNull()
    {
        // Arrange
        GitHubRelease[] releases = [new("models-v3", true, []), new("v1.0.0", false, []), new(null, false, null)];

        // Act
        var latest = ModelManifestClient.SelectLatest(releases);

        // Assert
        Assert.Null(latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetLatestAsync_RateLimited_FallsBackToDirectUrl(HttpStatusCode status)
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ReleasesPath => RateLimited(status),
            "/o/r/releases/download/models-v1/models.json" => ManifestFor("models-v1"),
            _ => ModelTestData.Status(HttpStatusCode.NotFound),
        });
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        var manifest = await client.GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v1", ManifestOrigin.Network), (manifest.Release, manifest.Origin));
    }

    [Fact]
    public async Task GetLatestAsync_Offline_ReturnsCachedCopy()
    {
        // Arrange: one online call fills the cache, then the network disappears.
        var online = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases(Release("models-v2")))
            : ManifestFor("models-v2"));
        await new ModelManifestClient(new HttpClient(online), Options()).GetLatestAsync(Ct);
        var offline = new FakeHttpHandler(_ => throw new HttpRequestException("No such host is known."));
        var client = new ModelManifestClient(new HttpClient(offline), Options());

        // Act
        var manifest = await client.GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v2", ManifestOrigin.Cache), (manifest.Release, manifest.Origin));
    }

    [Fact]
    public async Task GetLatestAsync_OfflineWithoutCache_ThrowsModelSourceUnavailableException()
    {
        // Arrange
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("No such host is known."));
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        var exception = await Record.ExceptionAsync(() => client.GetLatestAsync(Ct));

        // Assert
        Assert.IsType<ModelSourceUnavailableException>(exception);
    }

    [Fact]
    public async Task GetLatestAsync_CachedETag_SendsIfNoneMatchAndUsesCacheOn304()
    {
        // Arrange
        var first = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == ReleasesPath)
            {
                return ModelTestData.Json(Releases(Release("models-v1")));
            }

            var response = ManifestFor("models-v1");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            return response;
        });
        await new ModelManifestClient(new HttpClient(first), Options()).GetLatestAsync(Ct);

        var second = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases(Release("models-v1")))
            : request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"abc123\"")
                ? ModelTestData.Status(HttpStatusCode.NotModified)
                : ModelTestData.Status(HttpStatusCode.InternalServerError));
        var client = new ModelManifestClient(new HttpClient(second), Options());

        // Act
        var manifest = await client.GetLatestAsync(Ct);

        // Assert
        Assert.Equal(("models-v1", ManifestOrigin.NotModified), (manifest.Release, manifest.Origin));
    }

    [Fact]
    public async Task GetLatestAsync_Success_WritesCacheNextToModelsFolder()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases(Release("models-v1")))
            : ManifestFor("models-v1"));
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        await client.GetLatestAsync(Ct);

        // Assert
        Assert.True(File.Exists(_folder.CachePath));
    }

    [Fact]
    public async Task GetLatestAsync_UnsupportedSchema_ThrowsUnsupportedManifestSchemaException()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == ReleasesPath
            ? ModelTestData.Json(Releases(Release("models-v5")))
            : ModelTestData.Json("""{ "schemaVersion": 5, "release": "models-v5", "models": [] }"""));
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        var exception = await Record.ExceptionAsync(() => client.GetLatestAsync(Ct));

        // Assert
        Assert.IsType<UnsupportedManifestSchemaException>(exception);
    }

    [Fact]
    public async Task GetLatestAsync_ReleaseWithoutManifestAsset_UsesConventionalAssetUrl()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ReleasesPath => ModelTestData.Json(Releases(Release("models-v4", withManifestAsset: false))),
            "/o/r/releases/download/models-v4/models.json" => ManifestFor("models-v4"),
            _ => ModelTestData.Status(HttpStatusCode.NotFound),
        });
        var client = new ModelManifestClient(new HttpClient(handler), Options());

        // Act
        var manifest = await client.GetLatestAsync(Ct);

        // Assert
        Assert.Equal("models-v4", manifest.Release);
    }
}
