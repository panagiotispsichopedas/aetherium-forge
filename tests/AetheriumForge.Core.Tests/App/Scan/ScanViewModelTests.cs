using AetheriumForge.App.Pages.Scan;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Scan;

public sealed class ScanViewModelTests
{
    private const string DefaultProject = @"D:\AetheriumForge\Projects\Skyrim Special Edition";

    private readonly FakeScanEnvironment _environment = new();
    private readonly FakeScanSession _session = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly FakeNavigation _navigation = new();

    private ScanViewModel Create() => new(_environment, _session, _navigation, _picker, postToUi: action => action());

    private async Task<ScanViewModel> CreateInitializedAsync()
    {
        var viewModel = Create();
        await viewModel.InitializeAsync();
        await viewModel.ProfileLoad;
        return viewModel;
    }

    [Fact]
    public async Task InitializeAsync_InstallFound_SelectsIt()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.Equal(ScanTestData.GameFolder, viewModel.SelectedInstall?.GameFolder);
    }

    [Fact]
    public async Task InitializeAsync_InstallFound_ShowsStoreAndVersion()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.Equal(("Steam", "SkyrimSE.exe 1.6.1170.0"), (viewModel.Installs[0].StoreText, viewModel.Installs[0].VersionText));
    }

    [Fact]
    public async Task InitializeAsync_NoInstalls_ShowsInlineError()
    {
        _environment.Installs.Clear();

        var viewModel = await CreateInitializedAsync();

        Assert.Contains("Browse", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_DetectionThrows_ShowsInlineError()
    {
        _environment.DetectError = new IOException("registry unreadable");

        var viewModel = await CreateInitializedAsync();

        Assert.Equal("registry unreadable", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task InitializeAsync_NoExistingProject_DefaultsToBestNonSystemDrive()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.Equal(DefaultProject, viewModel.ProjectFolder);
    }

    [Fact]
    public async Task InitializeAsync_ExistingProjectOnOtherDrive_ReusesIt()
    {
        _environment.ExistingFiles.Add(@"E:\AetheriumForge\Projects\Skyrim Special Edition\forge.db");

        var viewModel = await CreateInitializedAsync();

        Assert.Equal((@"E:\AetheriumForge\Projects\Skyrim Special Edition", "existing project"), (viewModel.ProjectFolder, viewModel.ProjectFolderReason));
    }

    [Fact]
    public async Task InitializeAsync_Drives_MarkTheProjectDrive()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.Equal([@"D:\"], viewModel.Drives.Where(d => d.HoldsProject).Select(d => d.Root));
    }

    [Fact]
    public async Task InitializeAsync_ProfileLoaded_ShowsPluginCount()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.StartsWith("3 plugins", viewModel.LoadOrderText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowseGame_InvalidFolder_ShowsErrorAndKeepsSelection()
    {
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\NotAGame");

        await viewModel.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal((ScanTestData.GameFolder, true), (viewModel.SelectedInstall?.GameFolder, viewModel.HasError));
    }

    [Fact]
    public async Task BrowseGame_ValidFolder_AddsAndSelectsIt()
    {
        _environment.ValidGameFolders.Add(@"F:\Games\Skyrim");
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\Games\Skyrim");

        await viewModel.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal((@"F:\Games\Skyrim", "Chosen folder", 2), (viewModel.SelectedInstall?.GameFolder, viewModel.SelectedInstall?.StoreText, viewModel.Installs.Count));
    }

    [Fact]
    public async Task ChooseProjectFolder_InsideGameFolder_IsRejected()
    {
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(ScanTestData.GameFolder + @"\Data\forge");

        await viewModel.ChooseProjectFolderCommand.ExecuteAsync(null);

        Assert.Equal((DefaultProject, true), (viewModel.ProjectFolder, viewModel.HasError));
    }

    [Fact]
    public async Task ChooseProjectFolder_OutsideGame_IsUsed()
    {
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\Forge\Mine\");

        await viewModel.ChooseProjectFolderCommand.ExecuteAsync(null);

        Assert.Equal((@"F:\Forge\Mine", "chosen folder"), (viewModel.ProjectFolder, viewModel.ProjectFolderReason));
    }

    [Fact]
    public async Task ChooseProjectFolder_Cancelled_KeepsDefault()
    {
        var viewModel = await CreateInitializedAsync();

        await viewModel.ChooseProjectFolderCommand.ExecuteAsync(null);

        Assert.Equal(DefaultProject, viewModel.ProjectFolder);
    }

    [Fact]
    public async Task Scan_Completes_ImportsIntoProjectFolder()
    {
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal([DefaultProject], _session.Imports.Select(i => i.ProjectFolder));
    }

    [Fact]
    public async Task Scan_Completes_RaisesJobsChanged()
    {
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, _session.JobsChangedCount);
    }

    [Fact]
    public async Task Scan_Completes_SummaryMatchesScan()
    {
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal((6, 3, 3), (viewModel.Summary?.Gaps, viewModel.Summary?.Queued, viewModel.Summary?.Skipped));
    }

    [Fact]
    public async Task Scan_Completes_ProgressReachesHundred()
    {
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal((100d, false), (viewModel.ProgressValue, viewModel.IsScanning));
    }

    [Fact]
    public async Task Scan_ScannerThrows_ShowsErrorAndDoesNotImport()
    {
        _environment.ScanError = new InvalidDataException("bad archive");
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(("bad archive", 0), (viewModel.ErrorMessage, _session.Imports.Count));
    }

    [Fact]
    public async Task CancelScan_DuringScan_StopsWithoutImport()
    {
        _environment.ScanGate = new TaskCompletionSource();
        var viewModel = await CreateInitializedAsync();

        var scan = viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.CancelScanCommand.Execute(null);
        await scan;

        Assert.Equal((0, false, false), (_session.Imports.Count, viewModel.IsScanning, viewModel.HasError));
    }

    [Fact]
    public async Task Scan_ProjectWithStoredRules_UsesThem()
    {
        var stored = ForgeRules.Default with { MinSource = 512 };
        _environment.ExistingFiles.Add(DefaultProject + @"\forge.db");
        _environment.ProjectValues[ProjectKeys.RulesJson] = stored.ToJson();
        var viewModel = await CreateInitializedAsync();

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(512, _environment.RulesSeen?.MinSource);
    }

    [Fact]
    public async Task Scan_NoInstallSelected_CannotExecute()
    {
        _environment.Installs.Clear();

        var viewModel = await CreateInitializedAsync();

        Assert.False(viewModel.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Rescan_ProjectOpen_ScansIntoOpenProject()
    {
        var viewModel = await CreateInitializedAsync();
        await _session.OpenAsync(@"F:\Forge\Existing", TestContext.Current.CancellationToken);

        await viewModel.RescanCommand.ExecuteAsync(null);

        Assert.Equal([@"F:\Forge\Existing"], _session.Imports.Select(i => i.ProjectFolder));
    }

    [Fact]
    public async Task Rescan_NoProject_CannotExecute()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.False(viewModel.RescanCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenExisting_OpenFails_ShowsInlineError()
    {
        _session.OpenError = new ProjectException(@"F:\x is not an Aetherium Forge project (no forge.db).");
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\x");

        await viewModel.OpenExistingCommand.ExecuteAsync(null);

        Assert.Contains("not an Aetherium Forge project", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenExisting_ProjectWithJobs_ShowsSummaryFromJobs()
    {
        _session.Jobs.AddRange(
        [
            Job(@"textures\a.dds", JobState.Queued, null, 2048),
            Job(@"textures\b.dds", JobState.Done, null, 1024),
            Job(@"textures\c.dds", JobState.Skipped, "TooSmall", 0),
            Job(@"textures\d.dds", JobState.Skipped, JobStore.ProvidedByModError, 0),
        ]);
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\Forge\Existing");

        await viewModel.OpenExistingCommand.ExecuteAsync(null);
        await viewModel.SummaryLoad;

        Assert.Equal((3, 2, 1), (viewModel.Summary?.Gaps, viewModel.Summary?.Queued, viewModel.Summary?.Skipped));
    }

    [Fact]
    public async Task OpenExisting_Opened_UsesItAsProjectFolder()
    {
        var viewModel = await CreateInitializedAsync();
        _picker.Answers.Enqueue(@"F:\Forge\Existing");

        await viewModel.OpenExistingCommand.ExecuteAsync(null);

        Assert.Equal((@"F:\Forge\Existing", "open project"), (viewModel.ProjectFolder, viewModel.ProjectFolderReason));
    }

    [Fact]
    public async Task GoToQueue_ProjectOpen_NavigatesToQueue()
    {
        var viewModel = await CreateInitializedAsync();
        await _session.OpenAsync(@"F:\Forge\Existing", TestContext.Current.CancellationToken);

        viewModel.GoToQueueCommand.Execute(null);

        Assert.Equal(AppPage.Queue, _navigation.Current);
    }

    [Fact]
    public async Task GoToQueue_NoProject_CannotExecute()
    {
        var viewModel = await CreateInitializedAsync();

        Assert.False(viewModel.GoToQueueCommand.CanExecute(null));
    }

    private static JobRecord Job(string path, JobState state, string? error, int target) =>
        new(0, path, "Skyrim - Textures0.bsa", 512, 512, "BC1_UNORM", "Diffuse", state == JobState.Skipped ? null : "clutter", target, null, state, 0, error, null, null);
}
