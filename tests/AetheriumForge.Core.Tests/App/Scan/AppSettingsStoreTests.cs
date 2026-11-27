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

    [Fact]
    public async Task LoadAsync_InvalidEntries_AreDropped()
    {
        await File.WriteAllTextAsync(FilePath, """{ "recentProjects": [ "relative\\path", "", "D:\\Good" ] }""", Ct);
        var store = new AppSettingsStore(FilePath);

        var settings = await store.LoadAsync(Ct);

        Assert.Equal([@"D:\Good"], settings.RecentProjects);
    }

    [Fact]
    public void Constructor_TwoStoresSameFileDifferentCase_ShareOneProcessWideLock()
    {
        var first = new AppSettingsStore(FilePath);
        var second = new AppSettingsStore(FilePath.ToUpperInvariant());

        Assert.Same(AppSettingsStore.LockFor(first.FilePath), AppSettingsStore.LockFor(second.FilePath));
    }

    [Fact]
    public async Task SaveAsync_AnotherStoreHoldsTheFileLock_WaitsForIt()
    {
        var store = new AppSettingsStore(FilePath);
        var fileLock = AppSettingsStore.LockFor(new AppSettingsStore(FilePath).FilePath);
        await fileLock.WaitAsync(Ct);

        var save = store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1"] }, Ct);
        var completedWhileHeld = save.IsCompleted;
        fileLock.Release();
        await save;

        Assert.False(completedWhileHeld);
    }

    [Fact]
    public async Task SaveAsync_TargetHeldOpenUntilFirstRetry_RetriesAndSucceeds()
    {
        await File.WriteAllTextAsync(FilePath, "{}", Ct);
        var logger = new RetryLogger();
        var store = new AppSettingsStore(FilePath, logger);
        Task save;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            save = store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1"] }, Ct);
            await logger.FirstRetry.WaitAsync(Ct);
        }

        await save;

        Assert.Equal([@"D:\P1"], (await store.LoadAsync(Ct)).RecentProjects);
    }

    [Fact]
    public async Task SaveAsync_TargetHeldOpenThroughout_FailsAfterBoundedRetries()
    {
        await File.WriteAllTextAsync(FilePath, "{}", Ct);
        var logger = new RetryLogger();
        var store = new AppSettingsStore(FilePath, logger);
        Exception? error;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            error = await Record.ExceptionAsync(() => store.SaveAsync(new AppSettings { RecentProjects = [@"D:\P1"] }, Ct));
        }

        Assert.Equal((true, 7), (error is IOException or UnauthorizedAccessException, logger.Retries));
    }

    /// <summary>Counts the store's move retries (event 3) and signals the first one.</summary>
    private sealed class RetryLogger : Microsoft.Extensions.Logging.ILogger<AppSettingsStore>
    {
        private readonly TaskCompletionSource _firstRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _retries;

        public Task FirstRetry => _firstRetry.Task;

        public int Retries => Volatile.Read(ref _retries);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 3)
            {
                Interlocked.Increment(ref _retries);
                _firstRetry.TrySetResult();
            }
        }
    }
}
