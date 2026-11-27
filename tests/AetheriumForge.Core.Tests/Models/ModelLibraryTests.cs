using System.Net;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelLibraryTests : IDisposable
{
    private const string ReleasesPath = "/repos/o/r/releases";

    private readonly TempModelsFolder _folder = new();
    private readonly byte[] _current = ModelTestData.RandomBytes(5000, seed: 1);
    private readonly byte[] _older = ModelTestData.RandomBytes(5000, seed: 2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private ModelLibrary Library(bool online = true)
    {
        var manifest = ModelTestData.ManifestJson(
            "models-v2",
            ("current", ModelTestData.Sha256(_current), _current.Length),
            ("updated", ModelTestData.Sha256(_current), _current.Length),
            ("missing", ModelTestData.Sha256(_current), _current.Length));
        var handler = new FakeHttpHandler(request =>
        {
            if (!online)
            {
                throw new HttpRequestException("offline");
            }

            return request.RequestUri!.AbsolutePath switch
            {
                ReleasesPath => ModelTestData.Json("""[{ "tag_name": "models-v2", "draft": false, "assets": [] }]"""),
                "/o/r/releases/download/models-v2/models.json" => ModelTestData.Json(manifest),
                _ => ModelTestData.Status(HttpStatusCode.NotFound),
            };
        });
        var options = new ModelSourceOptions { Owner = "o", Repo = "r", ModelsFolder = _folder.ModelsFolder };
        var http = new HttpClient(handler);
        return new ModelLibrary(_folder.ModelsFolder, new ModelManifestClient(http, options), new ModelDownloader(http, _folder.ModelsFolder));
    }

    private async Task InstallAsync(string name, byte[] data)
    {
        var handler = new FakeHttpHandler(ModelTestData.WithLicence(request => ModelTestData.Serve(request, data)));
        await new ModelDownloader(new HttpClient(handler), _folder.ModelsFolder).DownloadAsync(ModelTestData.Model(name, data), null, PauseToken.None, Ct);
    }

    private static async Task<IReadOnlyDictionary<string, ModelState>> StatesAsync(ModelLibrary library) =>
        (await library.ListAsync(Ct)).ToDictionary(entry => entry.Name, entry => entry.State);

    [Fact]
    public async Task ListAsync_MixedFolder_ReportsEveryState()
    {
        // Arrange
        await InstallAsync("current", _current);
        await InstallAsync("updated", _older);
        await File.WriteAllBytesAsync(Path.Combine(_folder.ModelsFolder, "mine.onnx"), [1, 2, 3], Ct);

        // Act
        var states = await StatesAsync(Library());

        // Assert
        Assert.Equal(
            new Dictionary<string, ModelState>
            {
                ["current"] = ModelState.Installed,
                ["updated"] = ModelState.UpdateAvailable,
                ["missing"] = ModelState.NotInstalled,
                ["mine"] = ModelState.Custom,
            },
            states);
    }

    [Fact]
    public async Task ListAsync_InstalledWithoutRecordedHash_HashesFileToDecideState()
    {
        // Arrange: same bytes as the manifest, but no sidecar.
        await File.WriteAllBytesAsync(Path.Combine(_folder.ModelsFolder, "current.onnx"), _current, Ct);

        // Act
        var states = await StatesAsync(Library());

        // Assert
        Assert.Equal(ModelState.Installed, states["current"]);
    }

    [Fact]
    public async Task ListAsync_Offline_ListsInstalledModelsOnly()
    {
        // Arrange
        await InstallAsync("current", _current);
        await File.WriteAllBytesAsync(Path.Combine(_folder.ModelsFolder, "mine.onnx"), [1, 2, 3], Ct);

        // Act
        var states = await StatesAsync(Library(online: false));

        // Assert
        Assert.Equal(
            new Dictionary<string, ModelState> { ["current"] = ModelState.Installed, ["mine"] = ModelState.Custom },
            states);
    }

    [Fact]
    public async Task ListAsync_PartialDownload_IsFlagged()
    {
        // Arrange
        await File.WriteAllBytesAsync(Path.Combine(_folder.ModelsFolder, "missing.onnx.partial"), [1, 2, 3], Ct);

        // Act
        var entry = (await Library().ListAsync(Ct)).Single(candidate => candidate.Name == "missing");

        // Assert
        Assert.Equal((ModelState.NotInstalled, true), (entry.State, entry.HasPartialDownload));
    }

    [Fact]
    public async Task DeleteAsync_InstalledModel_RemovesModelAndSidecarButKeepsLicence()
    {
        // Arrange
        await InstallAsync("current", _current);
        var library = Library();

        // Act
        var deleted = await library.DeleteAsync("current", Ct);

        // Assert
        Assert.Equal(
            (true, false, false, true),
            (deleted,
             File.Exists(Path.Combine(_folder.ModelsFolder, "current.onnx")),
             File.Exists(Path.Combine(_folder.ModelsFolder, "current.json")),
             File.Exists(Path.Combine(_folder.ModelsFolder, "LICENSE.txt"))));
    }

    [Fact]
    public async Task DeleteAsync_UnknownModel_ReturnsFalse()
    {
        // Act
        var deleted = await Library().DeleteAsync("nothing", Ct);

        // Assert
        Assert.False(deleted);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    public async Task DeleteAsync_PathInName_ThrowsArgumentException(string name)
    {
        // Act
        var exception = await Record.ExceptionAsync(() => Library().DeleteAsync(name, Ct));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task ComputeInstalledSha256Async_InstalledModel_ReturnsLowercaseHex()
    {
        // Arrange
        await InstallAsync("current", _current);

        // Act
        var sha = await Library().ComputeInstalledSha256Async("current", Ct);

        // Assert
        Assert.Equal(ModelTestData.Sha256(_current), sha);
    }

    [Fact]
    public void AddModelDownloads_Configured_ResolvesLibraryWithConfiguredFolder()
    {
        // Arrange
        var services = new ServiceCollection().AddModelDownloads(options => options.ModelsFolder = _folder.ModelsFolder);
        using var provider = services.BuildServiceProvider();

        // Act
        var library = provider.GetRequiredService<ModelLibrary>();

        // Assert
        Assert.Equal(Path.GetFullPath(_folder.ModelsFolder), library.ModelsFolder);
    }

    [Fact]
    public void ResolveCachePath_Default_IsInParentOfModelsFolder()
    {
        // Arrange
        var options = new ModelSourceOptions { ModelsFolder = _folder.ModelsFolder };

        // Act
        var path = options.ResolveCachePath();

        // Assert
        Assert.Equal(_folder.CachePath, path);
    }
}
