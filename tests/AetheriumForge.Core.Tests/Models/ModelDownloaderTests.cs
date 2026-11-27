using System.Net;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelDownloaderTests : IDisposable
{
    private const int ChunkSize = 4096;

    private readonly TempModelsFolder _folder = new();
    private readonly byte[] _data = ModelTestData.RandomBytes(10 * ChunkSize + 123);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private string PartialPath(string name = "m") => Path.Combine(_folder.ModelsFolder, $"{name}.onnx{ModelDownloader.PartialSuffix}");

    private ModelDownloader Downloader(FakeHttpHandler handler) => new(new HttpClient(handler), _folder.ModelsFolder, ModelTestData.Options(_folder.ModelsFolder));

    private static bool IsModelRequest(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal);

    private FakeHttpHandler Server(bool honourRange = true, Action<ChunkedStream>? onStream = null) =>
        new(ModelTestData.WithLicence(request => ModelTestData.Serve(request, _data, honourRange, ChunkSize, onStream)));

    [Fact]
    public async Task DownloadAsync_FreshDownload_InstallsVerifiedFile()
    {
        // Arrange
        var downloader = Downloader(Server());

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(_data, await File.ReadAllBytesAsync(installed.Path, Ct));
    }

    [Fact]
    public async Task DownloadAsync_FreshDownload_RemovesPartialAndWritesLicence()
    {
        // Arrange
        var downloader = Downloader(Server());

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal(
            (false, ModelTestData.LicenceText),
            (File.Exists(PartialPath()), await File.ReadAllTextAsync(installed.LicencePath!, Ct)));
    }

    [Fact]
    public async Task DownloadAsync_TwoModels_DownloadsLicenceOnce()
    {
        // Arrange
        var handler = Server();
        var downloader = Downloader(handler);

        // Act
        await downloader.DownloadAsync(ModelTestData.Model("a", _data), null, PauseToken.None, Ct);
        await downloader.DownloadAsync(ModelTestData.Model("b", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Single(handler.RequestedUrls, url => url == ModelTestData.LicenceUrl);
    }

    [Fact]
    public async Task DownloadAsync_Success_SidecarIsReadByModelCatalog()
    {
        // Arrange
        var downloader = Downloader(Server());

        // Act
        await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        var info = Assert.Single(ModelCatalog.Discover(_folder.ModelsFolder));
        Assert.Equal(
            ("m", "m display", (int?)4, "fp16", "BSD-3-Clause", "diffuse,normal"),
            (info.Name, info.DisplayName, info.Scale, info.Precision, info.Licence, string.Join(',', info.RecommendedFor)));
    }

    [Fact]
    public async Task DownloadAsync_ExistingPartial_ResumesWithRangeAnd206()
    {
        // Arrange
        var resumeAt = 3 * ChunkSize + 7;
        await File.WriteAllBytesAsync(PartialPath(), _data[..resumeAt], Ct);
        var handler = Server();
        var downloader = Downloader(handler);

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        var modelRequest = handler.Requests.Single(request => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal));
        var written = await File.ReadAllBytesAsync(installed.Path, Ct);
        Assert.Equal(
            ((long?)resumeAt, true, true),
            (modelRequest.Headers.Range?.Ranges.Single().From, installed.Resumed, _data.AsSpan().SequenceEqual(written)));
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoresRange_RestartsFromZero()
    {
        // Arrange: the partial holds garbage; if it were kept the hash would fail.
        await File.WriteAllBytesAsync(PartialPath(), ModelTestData.RandomBytes(2 * ChunkSize, seed: 7), Ct);
        var downloader = Downloader(Server(honourRange: false));

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.Equal((false, _data.Length), (installed.Resumed, (await File.ReadAllBytesAsync(installed.Path, Ct)).Length));
    }

    [Fact]
    public async Task DownloadAsync_PartialLargerThanFile_RestartsWithoutRange()
    {
        // Arrange
        await File.WriteAllBytesAsync(PartialPath(), ModelTestData.RandomBytes(_data.Length + 10, seed: 9), Ct);
        var handler = Server();
        var downloader = Downloader(handler);

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        var modelRequest = handler.Requests.Single(request => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal));
        Assert.Equal((false, (RangeHeaderValueProbe?)null), (installed.Resumed, RangeHeaderValueProbe.From(modelRequest)));
    }

    [Fact]
    public async Task DownloadAsync_CompletePartial_VerifiesWithoutDownloading()
    {
        // Arrange
        await File.WriteAllBytesAsync(PartialPath(), _data, Ct);
        var handler = Server();
        var downloader = Downloader(handler);

        // Act
        var installed = await downloader.DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct);

        // Assert
        Assert.DoesNotContain(handler.RequestedUrls, url => url.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal));
        Assert.True(installed.Resumed);
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_ThrowsAndDeletesPartial()
    {
        // Arrange
        var model = ModelTestData.Model("m", _data, sha256: new string('0', 64));
        var downloader = Downloader(Server());

        // Act
        var exception = await Record.ExceptionAsync(() => downloader.DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(ModelHashMismatchException), false, false), (exception?.GetType(), File.Exists(PartialPath()), File.Exists(Path.Combine(_folder.ModelsFolder, "m.onnx"))));
    }

    [Fact]
    public async Task DownloadAsync_ServerSendsMoreThanManifestSize_ThrowsSizeMismatchAndDeletesPartial()
    {
        // Arrange: the manifest promises one byte less than the server sends.
        var model = ModelTestData.Model("m", _data, size: _data.Length - 1);
        var downloader = Downloader(Server());

        // Act
        var exception = await Record.ExceptionAsync(() => downloader.DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(ModelSizeMismatchException), false), (exception?.GetType(), File.Exists(PartialPath())));
    }

    [Fact]
    public async Task DownloadAsync_CleanButShortTransfer_ThrowsIOExceptionAndKeepsPartial()
    {
        // Arrange: the manifest promises one byte more than the server sends.
        var model = ModelTestData.Model("m", _data, size: _data.Length + 1);
        var downloader = Downloader(Server());

        // Act
        var exception = await Record.ExceptionAsync(() => downloader.DownloadAsync(model, null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(IOException), (long)_data.Length), (exception?.GetType(), new FileInfo(PartialPath()).Length));
    }

    [Fact]
    public async Task DownloadAsync_PausedAfterFirstChunk_StopsReadingUntilResumed()
    {
        // Arrange
        var pause = new PauseTokenSource();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChunkedStream? body = null;
        var progress = new SyncProgress(report =>
        {
            if (report.BytesDone > 0 && !paused.Task.IsCompleted)
            {
                pause.Pause();
                paused.SetResult();
            }
        });
        var handler = Server(onStream: stream => body ??= stream);
        var downloader = Downloader(handler);

        // Act
        var download = downloader.DownloadAsync(ModelTestData.Model("m", _data), progress, pause.Token, Ct);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(200, Ct);
        var readsWhilePaused = body!.Reads;
        var completedWhilePaused = download.IsCompleted;
        pause.Resume();
        await download.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert: the first body was abandoned after one read and the rest came from a new range request.
        var modelRequests = handler.Requests.Where(IsModelRequest).ToList();
        Assert.Equal(
            (1, false, true, 2, (long?)ChunkSize),
            (readsWhilePaused, completedWhilePaused, download.IsCompletedSuccessfully, modelRequests.Count, modelRequests[1].Headers.Range?.Ranges.Single().From));
    }

    [Fact]
    public async Task DownloadAsync_CancelledMidway_LeavesResumablePartial()
    {
        // Arrange
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress(report =>
        {
            if (report.BytesDone > 0)
            {
                cts.Cancel();
            }
        });
        var handler = Server();
        var model = ModelTestData.Model("m", _data);

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(model, progress, PauseToken.None, cts.Token));
        var partialLength = new FileInfo(PartialPath()).Length;
        var installed = await Downloader(handler).DownloadAsync(model, null, PauseToken.None, Ct);

        // Assert
        var resumeRequest = handler.Requests.Last(request => request.RequestUri!.AbsolutePath.EndsWith(".onnx", StringComparison.Ordinal));
        Assert.Equal(
            (true, (long?)partialLength, true),
            (exception is OperationCanceledException, resumeRequest.Headers.Range?.Ranges.Single().From, installed.Resumed));
    }

    [Fact]
    public async Task DownloadAsync_Progress_EndsWithTotalBytes()
    {
        // Arrange
        var progress = new SyncProgress(_ => { });
        var downloader = Downloader(Server());

        // Act
        await downloader.DownloadAsync(ModelTestData.Model("m", _data), progress, PauseToken.None, Ct);

        // Assert
        Assert.Equal((long)_data.Length, progress.Reports[^1].BytesDone);
    }

    [Fact]
    public async Task DownloadAsync_ServerError_KeepsPartialAndThrowsHttpRequestException()
    {
        // Arrange
        await File.WriteAllBytesAsync(PartialPath(), _data[..ChunkSize], Ct);
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(_ => ModelTestData.Status(HttpStatusCode.InternalServerError)));

        // Act
        var exception = await Record.ExceptionAsync(() => Downloader(handler).DownloadAsync(ModelTestData.Model("m", _data), null, PauseToken.None, Ct));

        // Assert
        Assert.Equal((typeof(HttpRequestException), (long)ChunkSize), (exception?.GetType(), new FileInfo(PartialPath()).Length));
    }

    /// <summary>Makes "no Range header" comparable in a tuple assertion.</summary>
    private sealed record RangeHeaderValueProbe(long? Start)
    {
        public static RangeHeaderValueProbe? From(HttpRequestMessage request) =>
            request.Headers.Range is { } range ? new RangeHeaderValueProbe(range.Ranges.Single().From) : null;
    }
}
