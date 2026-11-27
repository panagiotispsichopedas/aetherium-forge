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
        services.AddSingleton<ScanPagePlaceholder>(_ => new ScanPagePlaceholder(new PagePlaceholderViewModel("Scan")));

        // ── PAGE: Queue ──
        services.AddSingleton<QueuePagePlaceholder>(_ => new QueuePagePlaceholder(new PagePlaceholderViewModel("Queue")));

        // ── PAGE: Run (also owns IForgeRunner) ──
        // ForgeRunner (IForgeRunner), the log-tail sink (wired into the Serilog configuration above) and RunViewModel.
        services.AddForgeRunPage();
        services.AddSingleton<RunPagePlaceholder>(sp => new RunPagePlaceholder(sp.GetRequiredService<Pages.Run.RunViewModel>()));

        // ── PAGE: Models ──
        services.AddSingleton<ModelsPagePlaceholder>(_ => new ModelsPagePlaceholder(new PagePlaceholderViewModel("Models")));

        services.AddSingleton<IReadOnlyDictionary<AppPage, object>>(sp => new Dictionary<AppPage, object>
        {
            [AppPage.Scan] = sp.GetRequiredService<ScanPagePlaceholder>().ViewModel,
            [AppPage.Queue] = sp.GetRequiredService<QueuePagePlaceholder>().ViewModel,
            [AppPage.Run] = sp.GetRequiredService<RunPagePlaceholder>().ViewModel,
            [AppPage.Models] = sp.GetRequiredService<ModelsPagePlaceholder>().ViewModel,
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
}
