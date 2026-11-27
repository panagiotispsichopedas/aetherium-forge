using System.Net;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelDownloaderHardeningTests : IDisposable
{
    private const int ChunkSize = 4096;
    private const string ETagV1 = "\"v1\"";

    private readonly TempModelsFolder _folder = new();
    private readonly byte[] _data = ModelTestData.RandomBytes(10 * ChunkSize + 123);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private string ModelPath => Path.Combine(_folder.ModelsFolder, "m.onnx");

    private string PartialPath => ModelPath + ModelDownloader.PartialSuffix;

    private ModelDownloader Downloader(FakeHttpHandler handler) => new(new HttpClient(handler), _folder.ModelsFolder, ModelTestData.Options(_folder.ModelsFolder));

    private static bool IsModelRequest(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal);

    private static HttpResponseMessage RedirectedTo(HttpResponseMessage response, string finalUrl)
    {
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        return response;
    }

    private async Task<long> InterruptAfterFirstChunkAsync(FakeHttpHandler handler)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress(report =>
        {
            if (report.BytesDone > 0)
            {
                cts.Cancel();
            }
        });
        await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), progress, PauseToken.None, cts.Token));
        return new FileInfo(PartialPath).Length;
    }

    [Fact]
    public async Task DownloadAsync_RedirectedToUntrustedHost_ThrowsAndKeepsNothing()
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => RedirectedTo(ModelTestData.Serve(request, _data), "https://evil.example/m.onnx")));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(UntrustedModelSourceException), false, false), (exception?.GetType(), File.Exists(PartialPath), File.Exists(ModelPath)));
    }

    [Fact]
    public async Task DownloadAsync_RedirectedToGitHubAssetHost_Installs()
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request =>
            RedirectedTo(ModelTestData.Serve(request, _data), "https://release-assets.githubusercontent.com/github-production-release-asset/1/2")));

        // Act
        var installed = await Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.True(File.Exists(installed.Path));
    }

    [Fact]
    public async Task DownloadAsync_LicenceRedirectedToUntrustedHost_ThrowsBeforeModelRequest()
    {
        // Arrange
        var handler = new FakeHttpHandler(request => IsModelRequest(request)
            ? ModelTestData.Serve(request, _data)
            : RedirectedTo(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") }, "https://evil.example/LICENSE.txt"));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(UntrustedModelSourceException), false), (exception?.GetType(), handler.Requests.Any(IsModelRequest)));
    }

    [Fact]
    public async Task DownloadAsync_LicenceLargerThanCap_ThrowsInvalidDataException()
    {
        // Arrange: the content length is hidden so the streaming cap has to catch it.
        var big = new byte[ModelDownloader.MaxLicenceBytes + 1];
        var handler = new FakeHttpHandler(request => IsModelRequest(request)
            ? ModelTestData.Serve(request, _data)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(big, 64 * 1024)) });

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(InvalidDataException), false), (exception?.GetType(), File.Exists(Path.Combine(_folder.ModelsFolder, "LICENSE.txt"))));
    }

    [Theory]
    [InlineData("https://evil.example/o/r/releases/download/models-v1/m.onnx")]
    [InlineData("https://github.com/someone-else/r/releases/download/models-v1/m.onnx")]
    public async Task DownloadAsync_ModelUrlOutsideRepository_ThrowsWithoutRequest(string url)
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));
        var model = ModelTestData.Model("m", _data) with { Url = new Uri(url) };

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(ModelManifestException), 0), (exception?.GetType(), handler.Requests.Count));
    }

    [Theory]
    [InlineData("m.bin")]
    [InlineData("CON.onnx")]
    public async Task DownloadAsync_InvalidFileName_ThrowsModelManifestException(string fileName)
    {
        // Arrange
        var model = ModelTestData.Model(Path.GetFileNameWithoutExtension(fileName), _data) with { FileName = fileName };
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Fact]
    public async Task DownloadAsync_UppercaseSha256_ThrowsModelManifestException()
    {
        // Arrange
        var model = ModelTestData.Model("m", _data) with { Sha256 = ModelTestData.Sha256(_data).ToUpperInvariant() };
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Fact]
    public async Task DownloadAsync_SidecarCannotBeWritten_ModelIsNotInstalled()
    {
        // Arrange: a directory where the sidecar must go makes the sidecar move fail.
        Directory.CreateDirectory(Path.Combine(_folder.ModelsFolder, "m.json"));
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((true, false, true), (exception is IOException or UnauthorizedAccessException, File.Exists(ModelPath), File.Exists(PartialPath)));
    }

    [Fact]
    public async Task DownloadAsync_Resume_SendsIfRangeWithFirstResponseETag()
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, chunkSize: ChunkSize, etag: ETagV1)));
        await InterruptAfterFirstChunkAsync(handler);

        // Act
        var installed = await Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        var resume = handler.Requests.Last(IsModelRequest);
        Assert.Equal((ETagV1, true, false), (resume.Headers.IfRange?.EntityTag?.Tag, installed.Resumed, File.Exists(ModelPath + ModelDownloader.PartialMetaSuffix)));
    }

    [Fact]
    public async Task DownloadAsync_FileChangedSinceFirstResponse_RestartsFromZero()
    {
        // Arrange: the server now has ETag v2, so it answers the If-Range request with the whole file.
        var first = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, chunkSize: ChunkSize, etag: ETagV1)));
        await InterruptAfterFirstChunkAsync(first);
        var second = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, chunkSize: ChunkSize, etag: "\"v2\"")));

        // Act
        var installed = await Downloader(second).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal((false, _data.Length), (installed.Resumed, (await File.ReadAllBytesAsync(installed.Path, Ct)).Length));
    }

    [Fact]
    public async Task DownloadAsync_ContentRangeTotalDiffers_RestartsFromZero()
    {
        // Arrange
        await File.WriteAllBytesAsync(PartialPath, _data[..ChunkSize], Ct);
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, reportedTotal: _data.Length + 5)));

        // Act
        var installed = await Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        var modelRequests = handler.Requests.Where(IsModelRequest).ToList();
        Assert.Equal((false, 2, (object?)null), (installed.Resumed, modelRequests.Count, modelRequests[1].Headers.Range));
    }

    [Fact]
    public async Task DownloadAsync_PartialMetaForOtherFile_DiscardsPartial()
    {
        // Arrange: a partial left by a different release of the same file name.
        var other = ModelTestData.RandomBytes(_data.Length, seed: 99);
        var firstHandler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, other, chunkSize: ChunkSize)));
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var progress = new SyncProgress(report =>
            {
                if (report.BytesDone > 0)
                {
                    cts.Cancel();
                }
            });
            await Record.ExceptionAsync(() => Downloader(firstHandler).DownloadAsync(ModelTestData.Model("m", other), progress, PauseToken.None, cts.Token));
        }

        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));

        // Act
        var installed = await Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal((false, (object?)null), (installed.Resumed, handler.Requests.Single(IsModelRequest).Headers.Range));
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentCallsForSameFile_ShareOneTransfer()
    {
        // Arrange: the first call holds at the pause gate so the second one arrives while it runs.
        var pause = new PauseTokenSource();
        pause.Pause();
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data)));
        var model = ModelTestData.Model("m", _data);

        // Act
        var first = Downloader(handler).DownloadAsync(model, null, pause.Token, Ct);
        var second = Downloader(handler).DownloadAsync(model, null, PauseToken.None, Ct);
        await Task.Delay(100, Ct);
        pause.Resume();
        var results = await Task.WhenAll(first, second);

        // Assert
        Assert.Equal((1, results[0]), (handler.Requests.Count(IsModelRequest), results[1]));
    }

    [Fact]
    public async Task DownloadAsync_BodyStalls_ThrowsIOExceptionAndKeepsPartial()
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(_data[..ChunkSize])) }));
        var downloader = Downloader(handler);
        downloader.IdleTimeout = TimeSpan.FromMilliseconds(200);

        // Act
        var exception = await Record.ExceptionAsync(() => downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(IOException), (long)ChunkSize), (exception?.GetType(), new FileInfo(PartialPath).Length));
    }

    [Fact]
    public async Task DownloadAsync_Success_LeavesNoTemporaryFiles()
    {
        // Arrange
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, etag: ETagV1)));

        // Act
        await Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(
            "LICENSE.txt|m.json|m.onnx",
            string.Join('|', Directory.GetFiles(_folder.ModelsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Constructor_Default_UsesThisRepositoryAsTrustedSource()
    {
        // Arrange
        var model = ModelTestData.Model("m", _data);
        var downloader = new ModelDownloader(new HttpClient(new FakeHttpHandler(_ => ModelTestData.Status(HttpStatusCode.OK))), _folder.ModelsFolder);

        // Act: the test model points at o/r, which the default source does not trust.
        var exception = await Record.ExceptionAsync(() => downloader.DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }
}
