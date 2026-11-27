using AetheriumForge.App.Pages.Scan;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.App.Scan;

/// <summary>Builders for scan results and installs used by the Scan page tests.</summary>
internal static class ScanTestData
{
    public const string GameFolder = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition";

    public static GameInstall Steam { get; } = new(GameFolder, GameFolder + @"\Data", GameStore.Steam, new Version(1, 6, 1170, 0));

    public static ScannedTexture Queued(string path, string group, TextureKind kind, int target) =>
        new(path, "Skyrim - Textures0.bsa", null, new TextureDecision(SkipReason.None, kind, group, target, target, "model-a", 1.0));

    public static ScannedTexture Skipped(string path, SkipReason reason) =>
        new(path, "Skyrim - Textures0.bsa", null, new TextureDecision(reason, TextureKind.Diffuse, null, 0, 0, null, 0));

    public static ScanResult Result(params ScannedTexture[] textures) => new(textures, 10, 2, 18, []);

    public static ScanResult Sample() => Result(
        Queued(@"textures\a.dds", "architecture", TextureKind.Diffuse, 2048),
        Queued(@"textures\b_n.dds", "architecture", TextureKind.Normal, 2048),
        Queued(@"textures\c.dds", "clutter", TextureKind.Diffuse, 1024),
        Skipped(@"textures\d.dds", SkipReason.TooSmall),
        Skipped(@"textures\e.dds", SkipReason.TooSmall),
        Skipped(@"textures\f.dds", SkipReason.Cubemap));
}

/// <summary>A scripted <see cref="IScanEnvironment"/>.</summary>
internal sealed class FakeScanEnvironment : IScanEnvironment
{
    public List<GameInstall> Installs { get; } = [ScanTestData.Steam];

    public HashSet<string> ValidGameFolders { get; } = new(StringComparer.OrdinalIgnoreCase) { ScanTestData.GameFolder };

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ProjectValues { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<(string Root, long Free, bool IsSystem)> Drives { get; set; } =
    [
        (@"C:\", 500L << 30, true),
        (@"D:\", 900L << 30, false),
        (@"E:\", 100L << 30, false),
    ];

    public ScanResult Result { get; set; } = ScanTestData.Sample();

    public Exception? ScanError { get; set; }

    public Exception? DetectError { get; set; }

    /// <summary>When set, the scan waits for this before returning (so a test can cancel it).</summary>
    public TaskCompletionSource? ScanGate { get; set; }

    public ForgeRules? RulesSeen { get; private set; }

    public int ScanCount { get; private set; }

    public Task<IReadOnlyList<GameInstall>> FindInstallsAsync(CancellationToken cancellationToken) =>
        DetectError is { } error ? Task.FromException<IReadOnlyList<GameInstall>>(error) : Task.FromResult<IReadOnlyList<GameInstall>>([.. Installs]);

    public List<int> ValidateThreads { get; } = [];

    public GameInstall? ValidateInstall(string gameFolder, GameStore store) => RecordValidate(gameFolder, store);

    private GameInstall? RecordValidate(string gameFolder, GameStore store) => RecordThread() &&
        ValidGameFolders.Contains(Path.TrimEndingDirectorySeparator(gameFolder))
            ? new GameInstall(Path.TrimEndingDirectorySeparator(gameFolder), Path.Combine(gameFolder, "Data"), store, null)
            : null;

    private bool RecordThread()
    {
        lock (ValidateThreads)
        {
            ValidateThreads.Add(Environment.CurrentManagedThreadId);
        }

        return true;
    }

    public GameProfilePaths LocateProfile(GameStore store) => new(@"C:\Users\u\Documents\My Games\Skyrim Special Edition", @"C:\Users\u\AppData\Local\Skyrim Special Edition\plugins.txt");

    public Task<GameProfile> LoadProfileAsync(GameInstall game, GameProfilePaths paths, CancellationToken cancellationToken) =>
        Task.FromResult(new GameProfile(game.DataFolder, ["Skyrim.esm", "Update.esm", "Mod.esp"], ["Skyrim - Textures0.bsa"], ["Mod.bsa"]));

    public async Task<ScanResult> ScanAsync(GameProfile profile, ForgeRules rules, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        ScanCount++;
        RulesSeen = rules;
        progress.Report(new ScanProgress(ScanPhase.LooseFiles, 1, 2));
        progress.Report(new ScanProgress(ScanPhase.Headers, 2, 2));
        if (ScanGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ScanError is { } error ? throw error : Result;
    }

    public IReadOnlyList<(string Root, long Free, bool IsSystem)> GetFixedDrives() => Drives;

    public bool FileExists(string path) => ExistingFiles.Contains(path);

    public Task<string?> ReadProjectValueAsync(string databasePath, string key, CancellationToken cancellationToken) =>
        Task.FromResult(ProjectValues.TryGetValue(key, out var value) ? value : null);
}

/// <summary>An in-memory <see cref="IScanProjectSession"/>.</summary>
internal sealed class FakeScanSession : IScanProjectSession
{
    public event EventHandler? Changed;

    public event EventHandler? JobsChanged;

    public ProjectInfo? Current { get; set; }

    public JobStore? Store => null;

    public List<string> Recent { get; } = [];

    public bool ThrowOnRecent { get; set; }

    public string OpenGameFolder { get; set; } = ScanTestData.GameFolder;

    public IReadOnlyList<string> RecentProjects => ThrowOnRecent ? throw new InvalidOperationException("recent list broken") : Recent;

    public List<ScanImportRequest> Imports { get; } = [];

    public List<JobRecord> Jobs { get; } = [];

    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Exception? OpenError { get; set; }

    public int JobsChangedCount { get; private set; }

    public Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default)
    {
        if (OpenError is { } error)
        {
            return Task.FromException(error);
        }

        var root = ProjectLayout.For(projectFolder).Root;
        Current = new ProjectInfo(root, OpenGameFolder, OpenGameFolder + @"\Data", root + @"\output", root + @"\work", ForgeRules.Default);
        Recent.Insert(0, root);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<ScanImportResult> ApplyScanAsync(ScanImportRequest request, CancellationToken cancellationToken = default)
    {
        var layout = ProjectLayout.For(request.ProjectFolder);
        ProjectLocator.EnsureOutsideGame(layout, request.Game.GameFolder);
        Imports.Add(request);
        Current = new ProjectInfo(layout.Root, request.Game.GameFolder, request.Game.DataFolder, layout.OutputRoot, layout.WorkRoot, request.Rules);
        Changed?.Invoke(this, EventArgs.Empty);
        var counts = new Dictionary<JobState, int> { [JobState.Queued] = request.Result.Queued.Count() };
        return Task.FromResult(new ScanImportResult(layout.DatabasePath, request.Result.Textures.Count, 0, counts, request.Result.MissingArchives));
    }

    public Task<string?> GetProjectValueAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task<IReadOnlyList<JobRecord>> GetJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JobRecord>>([.. Jobs]);

    public Task<bool> ReopenLastAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void NotifyJobsChanged()
    {
        JobsChangedCount++;
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Returns scripted folders.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    public Queue<string?> Answers { get; } = new();

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
}

/// <summary>Records navigation.</summary>
internal sealed class FakeNavigation : INavigationService
{
    public event EventHandler<AppPage>? Navigated;

    public AppPage Current { get; private set; } = AppPage.Scan;

    public void NavigateTo(AppPage page)
    {
        Current = page;
        Navigated?.Invoke(this, page);
    }
}

/// <summary>A scripted <see cref="IForgeRunner"/>.</summary>
internal sealed class FakeForgeRunner : IForgeRunner
{
    public event EventHandler<PipelineProgress>? ProgressChanged { add { } remove { } }

    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    public PipelineState State { get; private set; } = PipelineState.Idle;

    public PipelineProgress? LastProgress => null;

    public void SetState(PipelineState state)
    {
        State = state;
        RunStateChanged?.Invoke(this, null);
    }

    public Task<PipelineRunSummary> StartAsync(ForgeRunRequest request) => throw new NotSupportedException();

    public void Pause() => State = PipelineState.Paused;

    public void Unpause()
    {
    }

    public void RequestStop()
    {
    }
}
