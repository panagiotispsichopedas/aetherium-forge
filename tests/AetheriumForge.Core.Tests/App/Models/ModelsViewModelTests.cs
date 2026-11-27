using AetheriumForge.App.Services;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Tests.Gpu;

namespace AetheriumForge.Core.Tests.App.Models;

public sealed class ModelsViewModelTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitForDownloadAsync(ModelsPageFixture fixture, string name)
    {
        await fixture.Downloads.Find(name)!.Completion.WaitAsync(_timeout, Ct);
        var background = await fixture.Ui.ReadAsync(() => fixture.ViewModel.BackgroundWork);
        await background.WaitAsync(_timeout, Ct);
    }

    [Fact]
    public async Task RefreshAsync_MixedFolder_MapsEveryState()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.X4Plus);
        fixture.Install(ModelsPageFixture.X4PlusFp16, ModelTestDataBytes(1234), "fp16");
        File.WriteAllBytes(fixture.ModelPath("my-custom"), ModelTestDataBytes(100));

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.Equal(
            ["Installed", "Update available", "Available", "Custom"],
            fixture.ViewModel.Models.Select(row => row.StateText));
    }

    [Fact]
    public async Task RefreshAsync_AvailableModel_ShowsSizePrecisionTierAndLicence()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        var row = fixture.Row(ModelsPageFixture.X4PlusFp16);
        Assert.Equal(
            ("64.0 KB", "FP16", "Standard", "BSD-3-Clause", "https://github.com/o/r/releases/download/models-v1/LICENSE.txt", "Download"),
            (row.SizeText, row.Precision, row.Tier, row.Licence, row.LicenceUrl?.ToString(), row.DownloadLabel));
    }

    [Fact]
    public async Task RefreshAsync_OfflineWithoutCache_WarnsThatTheListIsUnavailable()
    {
        // Arrange
        using var fixture = new ModelsPageFixture { Online = false };

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.Equal(
            (true, true),
            (fixture.ViewModel.IsManifestWarning, fixture.ViewModel.ManifestStatus!.Contains("could not be loaded", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RefreshAsync_OfflineWithCachedManifest_WarnsThatTheCachedListIsShown()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        fixture.Online = false;

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.Equal(
            (true, true, 3),
            (fixture.ViewModel.IsManifestWarning, fixture.ViewModel.ManifestStatus!.Contains("cached model list", StringComparison.Ordinal), fixture.ViewModel.Models.Count));
    }

    [Fact]
    public async Task DownloadCommand_Always_ConfirmsWithSizeAndLicence()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Dialogs.ConfirmDownload = false;
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DownloadCommand.ExecuteAsync(null));

        // Assert
        var item = Assert.Single(Assert.Single(fixture.Dialogs.DownloadConfirmations));
        Assert.Equal(
            ($"{ModelsPageFixture.General} display", 48L * 1024, "BSD-3-Clause", "https://github.com/o/r/releases/download/models-v1/LICENSE.txt"),
            (item.DisplayName, item.Size, item.Licence, item.LicenceUrl?.ToString()));
    }

    [Fact]
    public async Task DownloadCommand_Declined_DoesNotDownload()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Dialogs.ConfirmDownload = false;
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DownloadCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal((0, (ModelDownloadOperation?)null), (fixture.ModelRequests.Count(), fixture.Downloads.Find(ModelsPageFixture.General)));
    }

    [Fact]
    public async Task DownloadCommand_Confirmed_InstallsModelAndOffersCalibration()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DownloadCommand.ExecuteAsync(null));
        await WaitForDownloadAsync(fixture, ModelsPageFixture.General);

        // Assert
        Assert.Equal(
            (ModelState.Installed, true, ModelsPageFixture.General),
            (fixture.Row(ModelsPageFixture.General).State, File.Exists(fixture.ModelPath(ModelsPageFixture.General)), string.Join(',', fixture.Dialogs.CalibrationOffers)));
    }

    [Fact]
    public async Task PauseCommand_DuringDownload_ShowsPausedUntilResumed()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var row = fixture.Row(ModelsPageFixture.General);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Downloads.OperationChanged += (_, operation) =>
        {
            if (operation.Status == ModelDownloadStatus.Downloading && operation.Progress is { BytesDone: > 0 } && !paused.Task.IsCompleted)
            {
                row.PauseCommand.Execute(null);
                paused.TrySetResult();
            }
        };

        // Act
        await fixture.Ui.RunAsync(() => row.DownloadCommand.ExecuteAsync(null));
        await paused.Task.WaitAsync(_timeout, Ct);
        await Task.Delay(100, Ct);
        var whilePaused = await fixture.Ui.ReadAsync(() => (row.IsPaused, row.StateText, row.IsTransferActive, row.Download!.Completion.IsCompleted));
        row.ResumeCommand.Execute(null);
        await WaitForDownloadAsync(fixture, ModelsPageFixture.General);

        // Assert
        Assert.Equal(
            ((true, "Paused", true, false), ModelState.Installed),
            (whilePaused, fixture.Row(ModelsPageFixture.General).State));
    }

    [Fact]
    public async Task CancelCommand_DuringDownload_KeepsPartialThatTheNextDownloadResumes()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var cancelOnce = true;
        fixture.Downloads.OperationChanged += (_, operation) =>
        {
            if (cancelOnce && operation.Status == ModelDownloadStatus.Downloading && operation.Progress is { BytesDone: > 0 })
            {
                cancelOnce = false;
                fixture.Row(ModelsPageFixture.General).CancelCommand.Execute(null);
            }
        };
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DownloadCommand.ExecuteAsync(null));
        await WaitForDownloadAsync(fixture, ModelsPageFixture.General);
        var cancelled = fixture.Row(ModelsPageFixture.General);
        var afterCancel = (fixture.Downloads.Find(ModelsPageFixture.General)!.Status, cancelled.HasPartialDownload, cancelled.DownloadLabel, cancelled.StateText);

        // Act
        await fixture.Ui.RunAsync(() => cancelled.DownloadCommand.ExecuteAsync(null));
        await WaitForDownloadAsync(fixture, ModelsPageFixture.General);

        // Assert
        Assert.Equal(
            ((ModelDownloadStatus.Cancelled, true, "Resume download", "Partly downloaded"), true, ModelState.Installed),
            (afterCancel, fixture.Downloads.Find(ModelsPageFixture.General)!.Result!.Resumed, fixture.Row(ModelsPageFixture.General).State));
    }

    [Fact]
    public async Task DeleteCommand_Confirmed_DeletesModel()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DeleteCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal(
            (false, ModelState.NotInstalled, $"{ModelsPageFixture.General} installed"),
            (File.Exists(fixture.ModelPath(ModelsPageFixture.General)), fixture.Row(ModelsPageFixture.General).State, string.Join(',', fixture.Dialogs.DeleteConfirmations)));
    }

    [Fact]
    public async Task DeleteCommand_Declined_KeepsModel()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        fixture.Dialogs.ConfirmDelete = false;
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DeleteCommand.ExecuteAsync(null));

        // Assert
        Assert.True(File.Exists(fixture.ModelPath(ModelsPageFixture.General)));
    }

    [Fact]
    public async Task DeleteCommand_RunActive_RefusesWithoutAsking()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        fixture.Runner.SetState(PipelineState.Running);

        // Act
        await fixture.Ui.RunAsync(() => fixture.Row(ModelsPageFixture.General).DeleteCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal((0, true), (fixture.Dialogs.DeleteConfirmations.Count, File.Exists(fixture.ModelPath(ModelsPageFixture.General))));
    }

    [Fact]
    public async Task VerifyCommand_MatchingFile_ReportsVerified()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var row = fixture.Row(ModelsPageFixture.General);

        // Act
        await fixture.Ui.RunAsync(() => row.VerifyCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal("Verified: the SHA-256 matches the published file.", row.Message);
    }

    [Fact]
    public async Task VerifyCommand_FileChangedAfterListing_ReportsMismatch()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var row = fixture.Row(ModelsPageFixture.General);
        File.WriteAllBytes(fixture.ModelPath(ModelsPageFixture.General), ModelTestDataBytes(48 * 1024));

        // Act
        await fixture.Ui.RunAsync(() => row.VerifyCommand.ExecuteAsync(null));

        // Assert
        Assert.StartsWith("The SHA-256 does not match", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveCustomModelCommand_ValidForm_CopiesModelAndWritesSidecar()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var source = CustomSource(fixture, "my-model.onnx");
        fixture.Dialogs.PickedFile = source;
        await fixture.Ui.RunAsync(() => fixture.ViewModel.AddCustomModelCommand.ExecuteAsync(null));
        fixture.ViewModel.CustomDisplayName = "My model";
        fixture.ViewModel.CustomScale = 2;
        fixture.ViewModel.CustomPrecision = "fp16";

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.SaveCustomModelCommand.ExecuteAsync(null));

        // Assert
        var row = fixture.Row("my-model");
        Assert.Equal(
            (ModelState.Custom, "My model", "FP16", 2, true, false),
            (row.State, row.DisplayName, row.Precision, row.Entry.Installed!.Scale, File.ReadAllBytes(fixture.ModelPath("my-model")).SequenceEqual(File.ReadAllBytes(source)), fixture.ViewModel.IsCustomFormOpen));
    }

    [Theory]
    [InlineData("scale-zero")]
    [InlineData("scale-fraction")]
    [InlineData("empty-name")]
    [InlineData("bad-precision")]
    [InlineData("not-onnx")]
    [InlineData("name-collision")]
    [InlineData("already-in-folder")]
    [InlineData("unsafe-name")]
    public async Task SaveCustomModelCommand_InvalidForm_ShowsErrorAndCopiesNothing(string problem)
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var source = problem switch
        {
            "not-onnx" => CustomSource(fixture, "model.bin"),
            "name-collision" => CustomSource(fixture, $"{ModelsPageFixture.General}.onnx"),
            "unsafe-name" => CustomSource(fixture, "-bad name.onnx"),
            "already-in-folder" => fixture.ModelPath("in-place"),
            _ => CustomSource(fixture, "custom.onnx"),
        };
        File.WriteAllBytes(source, ModelTestDataBytes(16));
        var before = Directory.GetFiles(fixture.Folder.ModelsFolder).Length;
        fixture.ViewModel.OpenCustomForm(source);
        switch (problem)
        {
            case "scale-zero":
                fixture.ViewModel.CustomScale = 0;
                break;
            case "scale-fraction":
                fixture.ViewModel.CustomScale = 2.5m;
                break;
            case "empty-name":
                fixture.ViewModel.CustomDisplayName = "  ";
                break;
            case "bad-precision":
                fixture.ViewModel.CustomPrecision = "int8";
                break;
        }

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.SaveCustomModelCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal(
            (true, true, before),
            (fixture.ViewModel.CustomError is not null, fixture.ViewModel.IsCustomFormOpen, Directory.GetFiles(fixture.Folder.ModelsFolder).Length));
    }

    [Theory]
    [InlineData(8UL, false, GpuTier.Standard, ModelsPageFixture.X4Plus, 2)]
    [InlineData(2UL, false, GpuTier.Low, ModelsPageFixture.General, 1)]
    [InlineData(0UL, true, GpuTier.Low, ModelsPageFixture.General, 1)]
    public async Task RunFirstRunCheckAsync_NoModelsInstalled_RecommendsFamilyForTier(ulong dedicatedGiB, bool integrated, GpuTier tier, string family, int variants)
    {
        // Arrange
        var adapter = integrated ? FakeAdapterProvider.Integrated() : FakeAdapterProvider.Discrete(dedicatedGiB);
        using var fixture = new ModelsPageFixture(new FakeAdapterProvider(adapter));

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());

        // Assert
        var prompt = Assert.Single(fixture.Dialogs.FirstRunPrompts);
        Assert.Equal((tier, family, variants, adapter.Name), (prompt.Tier, prompt.Family, prompt.Items.Count, prompt.AdapterName));
    }

    [Fact]
    public async Task RunFirstRunCheckAsync_NoAdapter_RecommendsCompactModel()
    {
        // Arrange
        using var fixture = new ModelsPageFixture(new FakeAdapterProvider());

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());

        // Assert
        var prompt = Assert.Single(fixture.Dialogs.FirstRunPrompts);
        Assert.Equal((GpuTier.Low, ModelsPageFixture.General, (string?)null), (prompt.Tier, prompt.Family, prompt.AdapterName));
    }

    [Fact]
    public async Task RunFirstRunCheckAsync_ModelInstalled_DoesNotPrompt()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());

        // Assert
        Assert.Empty(fixture.Dialogs.FirstRunPrompts);
    }

    [Fact]
    public async Task RunFirstRunCheckAsync_DownloadRecommended_InstallsFamilyAndOffersCalibrationOnce()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Dialogs.FirstRunChoice = ModelFirstRunChoice.DownloadRecommended;

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());
        await WaitForDownloadAsync(fixture, ModelsPageFixture.X4Plus);
        await WaitForDownloadAsync(fixture, ModelsPageFixture.X4PlusFp16);

        // Assert
        Assert.Equal(
            (ModelState.Installed, ModelState.Installed, ModelState.NotInstalled, 0, ModelsPageFixture.X4Plus),
            (fixture.Row(ModelsPageFixture.X4Plus).State, fixture.Row(ModelsPageFixture.X4PlusFp16).State, fixture.Row(ModelsPageFixture.General).State, fixture.Dialogs.DownloadConfirmations.Count, string.Join(',', fixture.Dialogs.CalibrationOffers)));
    }

    [Fact]
    public async Task RunFirstRunCheckAsync_CalledTwice_PromptsOnce()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RunFirstRunCheckAsync());

        // Assert
        Assert.Single(fixture.Dialogs.FirstRunPrompts);
    }

    [Fact]
    public async Task RefreshAsync_Adapters_ListsSelectedAdapterAndTier()
    {
        // Arrange
        using var fixture = new ModelsPageFixture(new FakeAdapterProvider(FakeAdapterProvider.Integrated(), FakeAdapterProvider.Discrete(8)));

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.Equal(
            ("Integrated,Discrete", "Test GPU 8GB", (GpuTier?)GpuTier.Standard, "Test GPU 8GB (8.0 GB)"),
            (string.Join(',', fixture.ViewModel.Adapters.Select(row => row.KindText)), fixture.ViewModel.Adapters.Single(row => row.IsSelected).Name, fixture.ViewModel.Tier, fixture.ViewModel.SelectedAdapterText));
    }

    [Theory]
    [InlineData(3UL, true)]
    [InlineData(7UL, false)]
    public async Task RefreshAsync_AvailableVideoMemory_WarnsBelowHalfOfDedicated(ulong availableGiB, bool warns)
    {
        // Arrange
        var provider = new FakeAdapterProvider(FakeAdapterProvider.Discrete(8)) { Memory = new VideoMemoryInfo(availableGiB << 30, 0, availableGiB << 30, 0) };
        using var fixture = new ModelsPageFixture(provider);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.Equal(warns, fixture.ViewModel.HasVramWarning);
    }

    [Fact]
    public async Task RefreshAsync_ProfileFromOtherDriver_FlagsRecalibration()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Store.SaveAsync(Profile(ModelsPageFixture.General, "31.0.0.1"), Ct);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        var family = Assert.Single(fixture.ViewModel.Families);
        Assert.Equal((true, "Recalibration needed (driver changed)", "Recalibrate"), (family.NeedsRecalibration, family.ProfileText, family.CalibrateLabel));
    }

    [Fact]
    public async Task RefreshAsync_ProfileForCurrentDriver_ShowsIt()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Store.SaveAsync(Profile(ModelsPageFixture.General, FakeAdapterProvider.Discrete(8).DriverVersion!), Ct);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        Assert.StartsWith("FP32, tile 1024, 0.51 s/MP, calibrated", Assert.Single(fixture.ViewModel.Families).ProfileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_NotCalibrated_ShowsNotCalibrated()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());

        // Assert
        var family = Assert.Single(fixture.ViewModel.Families);
        Assert.Equal(("Not calibrated", "Calibrate"), (family.ProfileText, family.CalibrateLabel));
    }

    [Fact]
    public async Task CalibrateCommand_Idle_SavesProfileAndShowsIt()
    {
        // Arrange
        using var fixture = SimulatedModelsPage.Create(out _);
        fixture.Install(ModelsPageFixture.X4Plus);
        fixture.Install(ModelsPageFixture.X4PlusFp16, precision: "fp16");
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var family = Assert.Single(fixture.ViewModel.Families);

        // Act
        await fixture.Ui.RunAsync(() => family.CalibrateCommand.ExecuteAsync(null));

        // Assert
        var stored = await fixture.Store.FindAsync(SimulatedGpu.Adapter, ModelsPageFixture.X4Plus, Ct);
        Assert.Equal(
            (true, true, 100.0, false, "FP32 + FP16"),
            (stored is not null, Assert.Single(fixture.ViewModel.Families).IsCalibrated, fixture.ViewModel.CalibrationPercent, fixture.ViewModel.IsCalibrating, family.VariantsText));
    }

    [Fact]
    public async Task CancelCalibrationCommand_DuringCalibration_StopsWithoutSaving()
    {
        // Arrange
        using var fixture = SimulatedModelsPage.Create(out var gpu);
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        gpu.BeforeRun = run =>
        {
            if (run == 1)
            {
                fixture.ViewModel.CancelCalibrationCommand.Execute(null);
            }
        };

        // Act
        await fixture.Ui.RunAsync(() => Assert.Single(fixture.ViewModel.Families).CalibrateCommand.ExecuteAsync(null));

        // Assert
        Assert.Equal(
            ("Calibration cancelled.", 0),
            (fixture.ViewModel.CalibrationStatus, (await fixture.Store.ListAsync(Ct)).Count));
    }

    [Fact]
    public async Task CalibrateCommand_RunActive_IsDisabledUntilTheRunEnds()
    {
        // Arrange
        using var fixture = new ModelsPageFixture();
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        var family = Assert.Single(fixture.ViewModel.Families);

        // Act
        fixture.Runner.SetState(PipelineState.Running);
        var whileRunning = await fixture.Ui.ReadAsync(() => (family.CalibrateCommand.CanExecute(null), fixture.ViewModel.CanCalibrate));
        fixture.Runner.SetState(PipelineState.Idle);
        var afterRun = await fixture.Ui.ReadAsync(() => family.CalibrateCommand.CanExecute(null));

        // Assert
        Assert.Equal(((false, false), true), (whileRunning, afterRun));
    }

    [Fact]
    public async Task CalibrateCommand_RunActive_DoesNotCalibrate()
    {
        // Arrange
        using var fixture = SimulatedModelsPage.Create(out var gpu);
        fixture.Install(ModelsPageFixture.General);
        await fixture.Ui.RunAsync(() => fixture.ViewModel.RefreshAsync());
        fixture.Runner.SetState(PipelineState.Paused);

        // Act
        await fixture.Ui.RunAsync(() => fixture.ViewModel.CalibrateAsync(Assert.Single(fixture.ViewModel.Families)));

        // Assert
        Assert.Empty(gpu.Runs);
    }

    private static string CustomSource(ModelsPageFixture fixture, string fileName)
    {
        var folder = Path.Combine(fixture.Folder.Root, "import");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, ModelTestDataBytes(2048));
        return path;
    }

    private static byte[] ModelTestDataBytes(int length) => Core.Tests.Models.ModelTestData.RandomBytes(length, 99);

    private static GpuProfile Profile(string family, string driver)
    {
        var adapter = FakeAdapterProvider.Discrete(8);
        return new GpuProfile(adapter.Luid, adapter.Name, driver, 0, family, GpuProfiler.Fp32, 1024, 0.51, GpuTier.Standard, GpuProfiler.StandardTierModelFamily, ManualTimeProvider.Start)
        {
            VendorId = adapter.VendorId,
            DeviceId = adapter.DeviceId,
        };
    }
}
