using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Jobs;

/// <summary>Rescan safety: unreadable archives, running jobs and the single-transaction import.</summary>
public sealed class ScanImportSafetyTests : IAsyncLifetime
{
    private const string Textures0 = "Skyrim - Textures0.bsa";
    private const string Textures1 = "Skyrim - Textures1.bsa";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"af-import-safety-{Guid.NewGuid():N}.db");
    private JobStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _store = await JobStore.OpenAsync(_dbPath, Ct);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var file in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            File.Delete(file);
        }
    }

    private static ScannedTexture Texture(string path, string archive) =>
        new(path, archive, null, new TextureDecision(SkipReason.None, TextureKind.Diffuse, "g", 1024, 1024, "m", 1.0));

    private static ScanResult Result(IReadOnlyList<string> missing, params ScannedTexture[] textures) => new(textures, 0, 0, 2, missing);

    private async Task<JobRecord> JobAsync(string path)
    {
        await foreach (var job in _store.GetJobsAsync(null, Ct))
        {
            if (job.Path == path)
            {
                return job;
            }
        }

        throw new InvalidOperationException($"No job {path}.");
    }

    [Fact]
    public async Task ImportAsync_ArchiveNowUnreadable_KeepsItsJobsQueued()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0), Texture(@"textures\b.dds", Textures1)), Ct);

        await ScanImporter.ImportAsync(_store, Result([Textures1], Texture(@"textures\a.dds", Textures0)), Ct);

        Assert.Equal(JobState.Queued, (await JobAsync(@"textures\b.dds")).State);
    }

    [Fact]
    public async Task ImportAsync_ArchiveNowUnreadable_ReportsNoJobAsProvidedByMod()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0), Texture(@"textures\b.dds", Textures1)), Ct);

        var (_, nowProvided) = await ScanImporter.ImportAsync(_store, Result(["skyrim - textures1.bsa"], Texture(@"textures\a.dds", Textures0)), Ct);

        Assert.Equal(0, nowProvided);
    }

    [Fact]
    public async Task ImportAsync_TextureGoneFromReadableArchive_IsProvidedByMod()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0), Texture(@"textures\b.dds", Textures1)), Ct);

        await ScanImporter.ImportAsync(_store, Result(["Other.bsa"], Texture(@"textures\a.dds", Textures0)), Ct);

        Assert.Equal((JobState.Skipped, JobStore.ProvidedByModError), ((await JobAsync(@"textures\b.dds")).State, (await JobAsync(@"textures\b.dds")).Error));
    }

    [Fact]
    public async Task ApplyRescanAsync_RunningJob_IsNotTouched()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0)), Ct);
        await _store.MarkRunningAsync((await JobAsync(@"textures\a.dds")).Id, Ct);

        await _store.ApplyRescanAsync(new HashSet<string>(), Ct);

        Assert.Equal(JobState.Running, (await JobAsync(@"textures\a.dds")).State);
    }

    [Fact]
    public async Task ApplyRescanAsync_DoneJob_BecomesProvidedByMod()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0)), Ct);
        await _store.MarkDoneAsync((await JobAsync(@"textures\a.dds")).Id, 1.0, Ct);

        await _store.ApplyRescanAsync(new HashSet<string>(), Ct);

        Assert.Equal(JobState.Skipped, (await JobAsync(@"textures\a.dds")).State);
    }

    [Fact]
    public async Task ImportAsync_WithProjectValues_WritesThem()
    {
        await ScanImporter.ImportAsync(_store, Result([], Texture(@"textures\a.dds", Textures0)), new Dictionary<string, string> { [ProjectKeys.GamePath] = @"E:\Game" }, Ct);

        Assert.Equal(@"E:\Game", await _store.GetProjectValueAsync(ProjectKeys.GamePath, Ct));
    }

    [Fact]
    public async Task ImportScanAsync_FailsMidway_WritesNeitherJobsNorValues()
    {
        JobRecord[] jobs = [.. ScanImporter.ToJobRecords(Result([], Texture(@"textures\a.dds", Textures0))), null!];

        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.ImportScanAsync(
            jobs,
            new HashSet<string> { @"textures\a.dds" },
            new HashSet<string>(),
            new Dictionary<string, string> { [ProjectKeys.GamePath] = @"E:\Game" },
            Ct));

        Assert.Equal((0, (string?)null), ((await _store.CountByStateAsync(Ct)).Values.Sum(), await _store.GetProjectValueAsync(ProjectKeys.GamePath, Ct)));
    }
}
