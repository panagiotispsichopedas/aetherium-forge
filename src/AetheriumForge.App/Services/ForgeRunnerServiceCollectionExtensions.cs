using AetheriumForge.App.Pages.Run;
using AetheriumForge.App.Shell;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Services;

/// <summary>
/// Registers the Run page: <see cref="ForgeRunner"/> as <see cref="IForgeRunner"/>, the log-tail sink and <see cref="RunViewModel"/>.
/// </summary>
public static class ForgeRunnerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Run page services. <see cref="ForgeRunnerLogSink"/> is registered here; <see cref="AppHost"/>'s single Serilog configuration
    /// writes to it, so the log tail sees every log event.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddForgeRunPage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ForgeRunnerLogSink>();

        // Shared with the Models page; TryAdd keeps whichever registration came first.
        services.TryAddSingleton<IGpuAdapterProvider>(sp => new DxgiAdapterProvider(sp.GetRequiredService<ILogger<DxgiAdapterProvider>>()));
        services.TryAddSingleton(sp => new GpuProfileStore(null, sp.GetRequiredService<ILogger<GpuProfileStore>>()));

        services.AddSingleton<ForgeRunner>();
        services.AddSingleton<IForgeRunner>(sp => sp.GetRequiredService<ForgeRunner>());
        services.AddSingleton<RunViewModel>();
        return services;
    }
}
