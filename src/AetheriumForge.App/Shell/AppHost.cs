using AetheriumForge.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AetheriumForge.App.Shell;

/// <summary>
/// Builds the app's DI host: Serilog file logging, Core services, the shared app services and one view model per page.
/// </summary>
/// <remarks>
/// <para>M4 sessions each own one registration block below (marked <c>── PAGE: X ──</c>) and replace the placeholder with their real
/// view model and services. Keep edits inside your own block to avoid merge conflicts.</para>
/// </remarks>
public static class AppHost
{
    /// <summary>The per-user app data folder: <c>%LOCALAPPDATA%\AetheriumForge</c>.</summary>
    public static string AppDataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge");

    /// <summary>Builds the host (not started).</summary>
    /// <returns>The host.</returns>
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog((sp, logger) => logger
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppDataFolder, "logs", "app-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Sink(sp.GetRequiredService<ForgeRunnerLogSink>()));

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INavigationService, NavigationService>();

        // ── PAGE: Scan (also owns IProjectSession) ──
        services.AddSingleton(sp => new AppSettingsStore(Path.Combine(AppDataFolder, AppSettingsStore.FileName), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppSettingsStore>>()));
        services.AddSingleton<IAppSettingsStore>(sp => sp.GetRequiredService<AppSettingsStore>());
        services.AddSingleton(sp => new ProjectSession(sp.GetRequiredService<IAppSettingsStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProjectSession>>()));
        services.AddSingleton<IScanProjectSession>(sp => sp.GetRequiredService<ProjectSession>());
        services.AddSingleton<IProjectSession>(sp => sp.GetRequiredService<ProjectSession>());
        services.AddHostedService<ProjectSessionStartup>();
        services.AddSingleton<Pages.Scan.IScanEnvironment>(sp => new Pages.Scan.ScanEnvironment(
            new Core.Discovery.GameLocator(
                new Core.Discovery.WindowsRegistryReader(),
                new Core.Discovery.WindowsFileSystemProbe(),
                Core.Discovery.GameLocator.DefaultEpicManifestsFolder,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Core.Discovery.GameLocator>>()),
            new Core.Discovery.ProfileLocator(),
            new Core.Textures.TextureScanner(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Core.Textures.TextureScanner>>())));
        services.AddSingleton<Pages.Scan.IFolderPicker, Pages.Scan.StorageFolderPicker>();
        services.AddSingleton(sp => new Pages.Scan.ScanViewModel(
            sp.GetRequiredService<Pages.Scan.IScanEnvironment>(),
            sp.GetRequiredService<IScanProjectSession>(),
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<Pages.Scan.IFolderPicker>(),
            sp.GetService<IForgeRunner>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Pages.Scan.ScanViewModel>>()));
        services.AddSingleton<ScanPagePlaceholder>(sp => new ScanPagePlaceholder(sp.GetRequiredService<Pages.Scan.ScanViewModel>()));

        // ── PAGE: Queue ──
        // IProjectSession and IForgeRunner are resolved optionally: without a session the page shows "no project", without a runner
        // its actions are never blocked by a run.
        services.AddSingleton<Pages.Queue.QueueViewModel>(sp => new Pages.Queue.QueueViewModel(
            sp.GetService<IProjectSession>(),
            sp.GetService<IForgeRunner>(),
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<TimeProvider>(),
            new Pages.Queue.AvaloniaUiDispatcher(),
            () => Core.Imaging.ModelCatalog.Discover(Core.Imaging.ModelCatalog.DefaultModelsFolder),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Pages.Queue.QueueViewModel>>()));
        services.AddSingleton<QueuePagePlaceholder>(sp => new QueuePagePlaceholder(sp.GetRequiredService<Pages.Queue.QueueViewModel>()));

        // ── PAGE: Run (also owns IForgeRunner) ──
        // ForgeRunner (IForgeRunner), the log-tail sink (wired into the Serilog configuration above) and RunViewModel.
        services.AddForgeRunPage();
        services.AddSingleton<RunPagePlaceholder>(sp => new RunPagePlaceholder(sp.GetRequiredService<Pages.Run.RunViewModel>()));

        // ── PAGE: Models ──
        // AddModelsPage: AddModelDownloads, IGpuAdapterProvider (DXGI), GpuProfiler, GpuProfileStore and the GpuWorkLock singleton
        // (TryAdd: shared with Run), the download coordinator, the dialogs, the view model and the first-run prompt.
        services.AddModelsPage();
        services.AddSingleton<ModelsPagePlaceholder>(sp => new ModelsPagePlaceholder(sp.GetRequiredService<Pages.Models.ModelsViewModel>()));

        // ── PAGE: Rules (M5) ──
        // IStyleSelection is shared with Compare (TryAdd, so whichever block registers it first wins and both get the same instance).
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<AetheriumForge.App.Pages.Rules.IStyleSelection, AetheriumForge.App.Pages.Rules.StyleSelection>(services);
        services.AddSingleton<AetheriumForge.App.Pages.Rules.IRulesPageServices, AetheriumForge.App.Pages.Rules.AvaloniaRulesPageServices>();
        services.AddSingleton<AetheriumForge.App.Pages.Rules.RulesViewModel>();
        services.AddSingleton<RulesPagePlaceholder>(sp => new RulesPagePlaceholder(sp.GetRequiredService<AetheriumForge.App.Pages.Rules.RulesViewModel>()));

        // ── PAGE: Compare (M5) ──
        services.AddSingleton<ComparePagePlaceholder>(_ => new ComparePagePlaceholder(new PagePlaceholderViewModel("Compare")));

        services.AddSingleton<IReadOnlyDictionary<AppPage, object>>(sp => new Dictionary<AppPage, object>
        {
            [AppPage.Scan] = sp.GetRequiredService<ScanPagePlaceholder>().ViewModel,
            [AppPage.Queue] = sp.GetRequiredService<QueuePagePlaceholder>().ViewModel,
            [AppPage.Run] = sp.GetRequiredService<RunPagePlaceholder>().ViewModel,
            [AppPage.Models] = sp.GetRequiredService<ModelsPagePlaceholder>().ViewModel,
            [AppPage.Rules] = sp.GetRequiredService<RulesPagePlaceholder>().ViewModel,
            [AppPage.Compare] = sp.GetRequiredService<ComparePagePlaceholder>().ViewModel,
        });
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    // Holder types give each page a distinct DI key, so each session swaps exactly one registration and one dictionary line.
    internal sealed record ScanPagePlaceholder(object ViewModel);

    internal sealed record QueuePagePlaceholder(object ViewModel);

    internal sealed record RunPagePlaceholder(object ViewModel);

    internal sealed record ModelsPagePlaceholder(object ViewModel);

    internal sealed record RulesPagePlaceholder(object ViewModel);

    internal sealed record ComparePagePlaceholder(object ViewModel);
}
