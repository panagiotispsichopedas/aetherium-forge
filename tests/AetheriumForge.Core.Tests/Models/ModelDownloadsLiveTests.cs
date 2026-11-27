using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AetheriumForge.Core.Tests.Models;

/// <summary>
/// Talks to the real GitHub release. Skipped unless <c>AETHERIUM_TEST_NETWORK=1</c>.
/// </summary>
public sealed class ModelDownloadsLiveTests : IDisposable
{
    private const string NetworkVariable = "AETHERIUM_TEST_NETWORK";
    private const string CompactModel = "realesr-general-x4v3";

    private readonly TempModelsFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task DownloadAsync_LiveRelease_DownloadsVerifiesAndLoadsCompactModel()
    {
        // Arrange
        Assert.SkipUnless(Environment.GetEnvironmentVariable(NetworkVariable) == "1", $"Set {NetworkVariable}=1 to run live model download tests.");
        using var provider = new ServiceCollection()
            .AddModelDownloads(options => options.ModelsFolder = _folder.ModelsFolder)
            .BuildServiceProvider();
        var library = provider.GetRequiredService<ModelLibrary>();
        var entries = await library.ListAsync(Ct);
        var available = entries.Single(entry => entry.Name == CompactModel).Available!;

        var recorder = new FinalUrlRecorder(new SocketsHttpHandler());
        var options = new ModelSourceOptions { ModelsFolder = _folder.ModelsFolder };
        var downloader = new ModelDownloader(new HttpClient(recorder), _folder.ModelsFolder, options);

        // Act
        var installed = await downloader.DownloadAsync(available, null, PauseToken.None, Ct);
        using var runner = OnnxModelRunner.Create(installed.Path, new OnnxRunnerOptions { UseDirectML = false });

        // Assert: the asset really was redirected off github.com and the redirect target passed the host allowlist.
        var modelHost = recorder.FinalUrls.Last(url => !url.AbsolutePath.EndsWith(".txt", StringComparison.Ordinal)).Host;
        var states = (await library.ListAsync(Ct)).ToDictionary(entry => entry.Name, entry => entry.State);
        Assert.Equal(
            (true, available.Sha256, 4, ModelState.Installed, true, false),
            (library.LastManifest!.Release.StartsWith("models-v", StringComparison.Ordinal), installed.Sha256, runner.Scale, states[CompactModel],
             options.IsTrustedDownloadUrl(new Uri($"https://{modelHost}/")), string.Equals(modelHost, "github.com", StringComparison.OrdinalIgnoreCase)));
        TestContext.Current.SendDiagnosticMessage($"Model served from {modelHost}");
    }

    /// <summary>Records the URL each response ended on after redirects.</summary>
    private sealed class FinalUrlRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<Uri> FinalUrls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            FinalUrls.Add(response.RequestMessage?.RequestUri ?? request.RequestUri!);
            return response;
        }
    }
}