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
        builder.Services.AddSerilog((_, logger) => logger
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppDataFolder, "logs", "app-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INavigationService, NavigationService>();

        // ── PAGE: Scan (also owns IProjectSession) ──
        services.AddSingleton<ScanPagePlaceholder>(_ => new ScanPagePlaceholder(new PagePlaceholderViewModel("Scan")));

        // ── PAGE: Queue ──
        services.AddSingleton<QueuePagePlaceholder>(_ => new QueuePagePlaceholder(new PagePlaceholderViewModel("Queue")));

        // ── PAGE: Run (also owns IForgeRunner) ──
        services.AddSingleton<RunPagePlaceholder>(_ => new RunPagePlaceholder(new PagePlaceholderViewModel("Run")));

        // ── PAGE: Models ──
        services.AddSingleton<ModelsPagePlaceholder>(_ => new ModelsPagePlaceholder(new PagePlaceholderViewModel("Models")));

        // ── PAGE: Rules (M5) ──
        services.AddSingleton<RulesPagePlaceholder>(_ => new RulesPagePlaceholder(new PagePlaceholderViewModel("Rules")));

        // ── PAGE: Compare (M5) ──
        services.AddSingleton(sp => new Pages.Compare.ProjectPreviewBackend(sp.GetService<IProjectSession>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton(sp =>
        {
            // The runner is optional (the Run page registers it); without one there is no run to yield the GPU to.
            return new Pages.Compare.PreviewEngine(
                sp.GetRequiredService<Pages.Compare.ProjectPreviewBackend>(),
                () => sp.GetService<IForgeRunner>()?.State ?? Core.Pipeline.PipelineState.Idle,
                logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<Pages.Compare.PreviewEngine>>());
        });
        services.AddSingleton<Pages.Compare.ICompareProjectData>(sp => new Pages.Compare.SessionCompareProjectData(sp.GetService<IProjectSession>()));
        services.AddSingleton<Pages.Compare.ICompareDialogs, Pages.Compare.AvaloniaCompareDialogs>();
        services.AddSingleton(sp => new Pages.Compare.CompareViewModel(
            sp.GetRequiredService<Pages.Compare.PreviewEngine>(),
            sp.GetRequiredService<Pages.Compare.ICompareProjectData>(),
            sp.GetRequiredService<Pages.Compare.ICompareDialogs>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<Pages.Compare.CompareViewModel>>(),
            runner: sp.GetService<IForgeRunner>()));
        services.AddSingleton<ComparePagePlaceholder>(sp => new ComparePagePlaceholder(sp.GetRequiredService<Pages.Compare.CompareViewModel>()));

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
