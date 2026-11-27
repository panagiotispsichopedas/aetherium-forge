using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class ScanImporterTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"af-import-{Guid.NewGuid():N}.db");
    private JobStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await JobStore.OpenAsync(_dbPath, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var file in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ToJobRecords_QueuedDecision_MapsToQueuedJobWithMaxTarget()
    {
        // Arrange
        var result = Result(Texture(@"textures\a.dds", new TextureDecision(SkipReason.None, TextureKind.Diffuse, "Large", 2048, 1024, "m", 1.0)));

        // Act
        var job = ScanImporter.ToJobRecords(result).Single();

        // Assert
        Assert.Equal((JobState.Queued, 2048, "Large", "m", (string?)null), (job.State, job.Target, job.Group, job.Model, job.Error));
    }

    [Fact]
    public void ToJobRecords_SkippedDecision_MapsToSkippedJobWithReason()
    {
        var result = Result(Texture(@"textures\b_msn.dds", new TextureDecision(SkipReason.SkippedKind, TextureKind.ModelSpaceNormal, null, 0, 0, null, 0)));

        var job = ScanImporter.ToJobRecords(result).Single();

        Assert.Equal((JobState.Skipped, "SkippedKind"), (job.State, job.Error));
    }

    [Fact]
    public async Task ImportAsync_TextureNoLongerAGap_MarkedProvidedByMod()
    {
        // Arrange
        var queued = new TextureDecision(SkipReason.None, TextureKind.Diffuse, "Large", 1024, 1024, "m", 1.0);
        var ct = TestContext.Current.CancellationToken;
        await ScanImporter.ImportAsync(_store, Result(Texture(@"textures\a.dds", queued), Texture(@"textures\b.dds", queued)), ct);

        // Act
        var (_, nowProvidedByMod) = await ScanImporter.ImportAsync(_store, Result(Texture(@"textures\a.dds", queued)), ct);

        // Assert
        Assert.Equal(1, nowProvidedByMod);
    }

    private static ScannedTexture Texture(string path, TextureDecision decision) =>
        new(path, "Skyrim - Textures0.bsa", new DdsHeader(512, 256, 1, "DXT1", null, false), decision);

    private static ScanResult Result(params ScannedTexture[] textures) => new(textures, 0, 0, 1, []);
}
