using AetheriumForge.App.Pages.Models;
using AetheriumForge.Core.Concurrency;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Models;
using AetheriumForge.Native;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Services;

/// <summary>
/// Registers the Models page and the services behind it.
/// </summary>
public static class ModelsPageServiceCollectionExtensions
{
    /// <summary>
    /// Registers model downloads (<see cref="ModelDownloadsServiceCollectionExtensions.AddModelDownloads"/>), the DXGI
    /// <see cref="IGpuAdapterProvider"/>, <see cref="GpuProfiler"/> and <see cref="GpuProfileStore"/> (with <c>TryAdd</c>, because the Run
    /// page registers them too), the <see cref="ModelDownloadCoordinator"/>, the dialogs, the page settings
    /// (<c>%LOCALAPPDATA%\AetheriumForge\models-page.json</c>), <see cref="ModelsViewModel"/> and the first-run prompt.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddModelsPage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModelDownloads();
        services.TryAddSingleton<IGpuAdapterProvider>(sp => new DxgiAdapterProvider(sp.GetService<ILogger<DxgiAdapterProvider>>()));
        services.TryAddSingleton(sp => new GpuProfiler(sp.GetRequiredService<IGpuAdapterProvider>(), null, sp.GetService<TimeProvider>(), sp.GetService<ILogger<GpuProfiler>>()));
        services.TryAddSingleton(sp => new GpuProfileStore(null, sp.GetService<ILogger<GpuProfileStore>>()));
        services.TryAddSingleton(sp => new ModelDownloadCoordinator(sp.GetRequiredService<ModelLibrary>, sp.GetService<ILogger<ModelDownloadCoordinator>>()));
        services.TryAddSingleton<IModelDialogs, AvaloniaModelDialogs>();
        services.TryAddSingleton<IModelsPageSettings>(sp => new JsonModelsPageSettings(
            Path.Combine(Shell.AppHost.AppDataFolder, "models-page.json"),
            sp.GetService<ILogger<JsonModelsPageSettings>>()));
        // One GPU work lock per app (TryAdd: the Run page may register it too). ForgeRunner keeps its own instance today; both use the
        // same named mutex, so a calibration and a run still exclude each other.
        services.TryAddSingleton(sp => new GpuWorkLock("Aetherium Forge desktop app", sp.GetService<ILogger<GpuWorkLock>>()));
        services.TryAddSingleton(sp => new ModelsViewModel(
            sp.GetRequiredService<ModelLibrary>,
            sp.GetRequiredService<ModelDownloadCoordinator>(),
            sp.GetRequiredService<IGpuAdapterProvider>(),
            sp.GetRequiredService<GpuProfiler>(),
            sp.GetRequiredService<GpuProfileStore>(),
            sp.GetRequiredService<IModelDialogs>(),
            sp.GetRequiredService<IModelsPageSettings>(),
            sp.GetService<INavigationService>(),
            sp.GetService<IForgeRunner>(),
            sp.GetRequiredService<GpuWorkLock>(),
            action => Dispatcher.UIThread.Post(action),
            sp.GetService<ILogger<ModelsViewModel>>()));
        services.AddHostedService<ModelFirstRunService>();
        return services;
    }
}
