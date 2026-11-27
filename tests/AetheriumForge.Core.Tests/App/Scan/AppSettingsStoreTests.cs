using AetheriumForge.App.Services;

namespace AetheriumForge.Core.Tests.App.Scan;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("af-settings-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_folder, AppSettingsStore.FileName);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsDefaults()
    {
        var store = new AppSettingsStore(FilePath);

        var settings = await store.LoadAsync(Ct);

        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsDefaults()
    {
        await File.WriteAllTextAsync(FilePath, "{ \"recentProjects\": [ \"D:\\\\P\", ", Ct);
        var store = new AppSettingsStore(FilePath);

        var settings = await store.LoadAsync(Ct);

        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public async Task LoadAsync_WrongShape_ReturnsDefaults()
    {
        await File.WriteAllTextAsync(FilePath, "[1, 2, 3]", Ct);
        var store = new AppSettingsStore(FilePath);

        var settings = await store.LoadAsync(Ct);

        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_RoundTrips()
    {
        var store = new AppSettingsStore(FilePath);

        await store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1", @"E:\P2"] }, Ct);

        Assert.Equal([@"D:\P1", @"E:\P2"], (await store.LoadAsync(Ct)).RecentProjects);
    }

    [Fact]
    public async Task SaveAsync_OverCorruptFile_ReplacesIt()
    {
        await File.WriteAllTextAsync(FilePath, "not json", Ct);
        var store = new AppSettingsStore(FilePath);

        await store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1"] }, Ct);

        Assert.Equal([@"D:\P1"], (await store.LoadAsync(Ct)).RecentProjects);
    }

    [Fact]
    public async Task SaveAsync_Completed_LeavesNoTemporaryFile()
    {
        var store = new AppSettingsStore(FilePath);

        await store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1"] }, Ct);

        Assert.Equal([AppSettingsStore.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void PushRecent_ExistingEntryDifferentCase_MovesItToFront()
    {
        var recent = AppSettingsStore.PushRecent([@"D:\A", @"D:\B"], @"d:\b");

        Assert.Equal([@"d:\b", @"D:\A"], recent);
    }

    [Fact]
    public void PushRecent_FullList_KeepsMaximum()
    {
        var full = Enumerable.Range(0, AppSettingsStore.MaxRecentProjects).Select(i => $@"D:\P{i}").ToList();

        var recent = AppSettingsStore.PushRecent(full, @"D:\New");

        Assert.Equal((AppSettingsStore.MaxRecentProjects, @"D:\New"), (recent.Count, recent[0]));
    }
}
