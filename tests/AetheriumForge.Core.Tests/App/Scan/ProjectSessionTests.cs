using AetheriumForge.App.Services;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Scan;

public sealed class ProjectSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("af-session-").FullName;
    private readonly string _gameFolder;
    private readonly GameInstall _game;
    private readonly AppSettingsStore _settings;
    private readonly ProjectSession _session;

    public ProjectSessionTests()
    {
        _gameFolder = Path.Combine(_root, "Skyrim Special Edition");
        Directory.CreateDirectory(Path.Combine(_gameFolder, "Data"));
        _game = new GameInstall(_gameFolder, Path.Combine(_gameFolder, "Data"), GameStore.Steam, null);
        _settings = new AppSettingsStore(Path.Combine(_root, "settings", AppSettingsStore.FileName));
        _session = new ProjectSession(_settings, TimeProvider.System, postToUi: action => action());
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ProjectFolder(string name = "project") => Path.Combine(_root, "projects", name);

    private Task<ScanImportResult> ScanIntoAsync(string folder) =>
        _session.ApplyScanAsync(new ScanImportRequest(folder, _game, ForgeRules.Default, ScanTestData.Sample()), Ct);

    [Fact]
    public async Task ApplyScanAsync_NewFolder_CreatesProjectDatabase()
    {
        await ScanIntoAsync(ProjectFolder());

        Assert.True(File.Exists(Path.Combine(ProjectFolder(), ProjectLayout.DatabaseFileName)));
    }

    [Fact]
    public async Task ApplyScanAsync_NewFolder_ImportsQueuedAndSkipped()
    {
        var result = await ScanIntoAsync(ProjectFolder());

        Assert.Equal((3, 3), (result.Counts[JobState.Queued], result.Counts[JobState.Skipped]));
    }

    [Fact]
    public async Task ApplyScanAsync_NewFolder_StoresGamePathLikeTheCli()
    {
        await ScanIntoAsync(ProjectFolder());

        var gamePath = await ProjectDatabaseReader.ReadProjectValueAsync(Path.Combine(ProjectFolder(), ProjectLayout.DatabaseFileName), ProjectKeys.GamePath, Ct);
        Assert.Equal(_gameFolder, gamePath);
    }

    [Fact]
    public async Task ApplyScanAsync_NewFolder_OpensProjectAndRaisesChanged()
    {
        var changed = 0;
        _session.Changed += (_, _) => changed++;

        await ScanIntoAsync(ProjectFolder());

        Assert.Equal((ProjectFolder(), 1), (_session.Current?.Root, changed));
    }

    [Fact]
    public async Task ApplyScanAsync_FolderInsideGame_ThrowsAndWritesNothing()
    {
        var inside = Path.Combine(_gameFolder, "Data", "forge");

        await Assert.ThrowsAsync<ProjectException>(() => ScanIntoAsync(inside));

        Assert.False(Directory.Exists(inside));
    }

    [Fact]
    public async Task OpenAsync_ScannedProject_LoadsGameAndRules()
    {
        await ScanIntoAsync(ProjectFolder());
        using var reopened = new ProjectSession(_settings, TimeProvider.System, postToUi: action => action());

        await reopened.OpenAsync(ProjectFolder(), Ct);

        Assert.Equal((_gameFolder, ForgeRules.Default.MinSource), (reopened.Current?.GameFolder, reopened.Current?.Rules.MinSource));
    }

    [Fact]
    public async Task OpenAsync_FolderWithoutDatabase_ThrowsProjectException()
    {
        Directory.CreateDirectory(ProjectFolder("empty"));

        await Assert.ThrowsAsync<ProjectException>(() => _session.OpenAsync(ProjectFolder("empty"), Ct));
    }

    [Fact]
    public async Task OpenAsync_ProjectRecordedInsideGame_ThrowsProjectException()
    {
        var inside = Path.Combine(_gameFolder, "forge");
        Directory.CreateDirectory(inside);
        var store = await JobStore.OpenAsync(Path.Combine(inside, ProjectLayout.DatabaseFileName), Ct);
        await using (store)
        {
            await store.SetProjectValueAsync(ProjectKeys.GamePath, _gameFolder, Ct);
        }

        await Assert.ThrowsAsync<ProjectException>(() => _session.OpenAsync(inside, Ct));
    }

    [Fact]
    public async Task ApplyScanAsync_TwoProjects_RemembersMostRecentFirst()
    {
        await ScanIntoAsync(ProjectFolder("a"));
        await ScanIntoAsync(ProjectFolder("b"));

        Assert.Equal([ProjectFolder("b"), ProjectFolder("a")], _session.RecentProjects);
    }

    [Fact]
    public async Task ReopenLastAsync_AfterRestart_OpensMostRecentProject()
    {
        await ScanIntoAsync(ProjectFolder("a"));
        await ScanIntoAsync(ProjectFolder("b"));
        using var restarted = new ProjectSession(_settings, TimeProvider.System, postToUi: action => action());

        var opened = await restarted.ReopenLastAsync(Ct);

        Assert.Equal((true, ProjectFolder("b")), (opened, restarted.Current?.Root));
    }

    [Fact]
    public async Task ReopenLastAsync_NoRecentProjects_ReturnsFalse()
    {
        var opened = await _session.ReopenLastAsync(Ct);

        Assert.False(opened);
    }

    [Fact]
    public async Task GetJobsAsync_AfterScan_ReturnsEveryTexture()
    {
        await ScanIntoAsync(ProjectFolder());

        var jobs = await _session.GetJobsAsync(Ct);

        Assert.Equal(6, jobs.Count);
    }

    [Fact]
    public void NotifyJobsChanged_Subscribed_RaisesJobsChanged()
    {
        var raised = 0;
        _session.JobsChanged += (_, _) => raised++;

        _session.NotifyJobsChanged();

        Assert.Equal(1, raised);
    }

    // ── Review fixes ──

    [Fact]
    public async Task ApplyScanAsync_AbsentArchiveWithoutJobs_IsNotInstalledNotAWarning()
    {
        var result = await _session.ApplyScanAsync(
            new ScanImportRequest(ProjectFolder(), _game, ForgeRules.Default, ScanTestData.Sample() with { MissingArchives = ["Skyrim - Patch.bsa"] }),
            Ct);

        Assert.Equal((0, 1), (result.MissingArchives.Warnings.Count, result.MissingArchives.NotInstalled.Count));
    }

    [Fact]
    public async Task ApplyScanAsync_ArchiveThatProvidedJobsNowMissing_IsAWarning()
    {
        await ScanIntoAsync(ProjectFolder());

        var result = await _session.ApplyScanAsync(
            new ScanImportRequest(ProjectFolder(), _game, ForgeRules.Default, ScanTestData.Result() with { MissingArchives = ["Skyrim - Textures0.bsa"] }),
            Ct);

        Assert.Equal(["Skyrim - Textures0.bsa"], result.MissingArchives.Warnings);
    }

    [Fact]
    public async Task OpenAsync_SettingsCannotBeSaved_StillOpensAndRaisesChanged()
    {
        await ScanIntoAsync(ProjectFolder());
        using var session = new ProjectSession(new FailingSettings(), TimeProvider.System, postToUi: action => action());
        var changed = 0;
        session.Changed += (_, _) => changed++;

        await session.OpenAsync(ProjectFolder(), Ct);

        Assert.Equal((ProjectFolder(), 1), (session.Current?.Root, changed));
    }

    [Fact]
    public async Task ApplyScanAsync_Rejected_RaisesNoChanged()
    {
        var changed = 0;
        _session.Changed += (_, _) => changed++;

        await Assert.ThrowsAsync<ProjectException>(() => ScanIntoAsync(Path.Combine(_gameFolder, "forge")));

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task GetJobsAsync_DuringProjectSwitches_NeverSeesDisposedStore()
    {
        await ScanIntoAsync(ProjectFolder("a"));
        await ScanIntoAsync(ProjectFolder("b"));

        var work = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            work.Add(_session.OpenAsync(ProjectFolder(i % 2 == 0 ? "a" : "b"), Ct));
            work.Add(_session.GetJobsAsync(Ct));
            work.Add(_session.GetProjectValueAsync(ProjectKeys.GamePath, Ct));
        }

        var error = await Record.ExceptionAsync(() => Task.WhenAll(work));

        Assert.Null(error);
    }

    [Fact]
    public async Task ReopenLastAsync_InvalidRecentEntryFirst_OpensTheNextProject()
    {
        await ScanIntoAsync(ProjectFolder("a"));
        using var session = new ProjectSession(new FixedSettings(["bad\0path|", ProjectFolder("a")]), TimeProvider.System, postToUi: action => action());

        var opened = await session.ReopenLastAsync(Ct);

        Assert.Equal((true, ProjectFolder("a")), (opened, session.Current?.Root));
    }

    [Fact]
    public async Task Dispose_ThenOpen_ThrowsObjectDisposed()
    {
        await ScanIntoAsync(ProjectFolder());
        _session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _session.OpenAsync(ProjectFolder(), Ct));
    }

    [Fact]
    public async Task Dispose_Twice_ReleasesStoreOnce()
    {
        await ScanIntoAsync(ProjectFolder());

        _session.Dispose();
        _session.Dispose();

        Assert.Null(_session.Store);
    }

    private sealed class FailingSettings : IAppSettingsStore
    {
        public string FilePath => @"X:\settings.json";

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromException(new IOException("disk full"));
    }

    private sealed class FixedSettings(IReadOnlyList<string> recent) : IAppSettingsStore
    {
        public string FilePath => @"X:\settings.json";

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { RecentProjects = recent });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
