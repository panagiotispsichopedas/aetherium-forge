using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Pipeline;
using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AetheriumForge.Cli;

/// <summary>
/// Settings of one <c>forge</c> run, from the command line.
/// </summary>
/// <param name="ModelOverride">Replaces every job's model (<c>--model</c>), or <see langword="null"/>.</param>
/// <param name="Device">DirectML adapter index (<c>--device</c>); also passed to texconv.</param>
/// <param name="TileSize">Upscaler tile size in pixels (<c>--tile</c>).</param>
/// <param name="Limit">Process at most this many jobs (<c>--limit</c>), or <see langword="null"/>.</param>
/// <param name="Sequential">Use <see cref="SequentialPipeline"/> even when the staged pipeline is available (<c>--sequential</c>).</param>
internal sealed record ForgeRunSettings(string? ModelOverride, int Device, int TileSize, int? Limit, bool Sequential);

/// <summary>
/// What <c>forge</c> resolved before building the services.
/// </summary>
/// <param name="Store">The open job store (owned by the caller).</param>
/// <param name="Project">The project layout.</param>
/// <param name="DataFolder">The game's <c>Data</c> folder.</param>
/// <param name="Rules">The rules the project was scanned with.</param>
/// <param name="Models">Installed models.</param>
/// <param name="TexconvPath">The texconv executable.</param>
/// <param name="Run">Command-line settings.</param>
internal sealed record ForgeContext(JobStore Store, ProjectLayout Project, string DataFolder, ForgeRules Rules, IReadOnlyList<ModelInfo> Models, string TexconvPath, ForgeRunSettings Run);

/// <summary>
/// The CLI's single composition root: builds the host and registers every service. <see cref="IForgePipeline"/> is created by one
/// factory method, <see cref="CreatePipeline"/>.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>
    /// Builds the host.
    /// </summary>
    /// <param name="logging">The Serilog setup; not disposed by the host.</param>
    /// <param name="forge">Forge services to register, or <see langword="null"/> for commands that do not process textures.</param>
    /// <returns>The host; dispose it to release GPU sessions and archive readers.</returns>
    public static IHost Build(CliLogging logging, ForgeContext? forge = null)
    {
        ArgumentNullException.ThrowIfNull(logging);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var services = builder.Services;

        services.AddSerilog(logging.Logger, dispose: false);
        services.AddSingleton(TimeProvider.System);

        // Discovery and scan
        services.AddSingleton<IRegistryReader, WindowsRegistryReader>();
        services.AddSingleton<IFileSystemProbe, WindowsFileSystemProbe>();
        services.AddSingleton(sp => new GameLocator(
            sp.GetRequiredService<IRegistryReader>(),
            sp.GetRequiredService<IFileSystemProbe>(),
            GameLocator.DefaultEpicManifestsFolder,
            sp.GetRequiredService<ILogger<GameLocator>>()));
        services.AddSingleton(_ => new ProfileLocator());
        services.AddSingleton(sp => new TextureScanner(sp.GetRequiredService<ILogger<TextureScanner>>()));

        if (forge is not null)
        {
            AddForgeServices(services, forge);
        }

        return builder.Build();
    }

    private static void AddForgeServices(IServiceCollection services, ForgeContext forge)
    {
        services.AddSingleton(forge);
        services.AddSingleton(forge.Run);
        services.AddSingleton(forge.Store);
        services.AddSingleton(new PipelineOptions { WorkRoot = forge.Project.WorkRoot, OutputRoot = forge.Project.OutputRoot });

        services.AddSingleton<ITextureSource>(_ => new ArchiveTextureSource(forge.DataFolder));
        services.AddSingleton<ITextureCodec>(sp => new Texconv(
            new TexconvOptions { ExecutablePath = forge.TexconvPath, GpuAdapter = forge.Run.Device },
            sp.GetRequiredService<ILogger<Texconv>>()));
        services.AddSingleton(sp => new UpscalerFactory(
            forge.Models,
            new OnnxRunnerOptions { DeviceId = forge.Run.Device },
            new TilingOptions { TileSize = forge.Run.TileSize },
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp =>
        {
            // --model is applied here, at the upscaler lookup, so it works the same for every pipeline.
            var upscalers = sp.GetRequiredService<UpscalerFactory>();
            var modelOverride = forge.Run.ModelOverride;
            return new TextureProcessor(
                sp.GetRequiredService<ITextureSource>(),
                sp.GetRequiredService<ITextureCodec>(),
                model => upscalers.Get(string.IsNullOrWhiteSpace(modelOverride) ? model : modelOverride),
                forge.Project.OutputRoot,
                forge.Project.WorkRoot,
                sp.GetRequiredService<ILogger<TextureProcessor>>());
        });
        services.AddSingleton(sp => new JobMapper(forge.Rules, forge.Run.ModelOverride, sp.GetRequiredService<ILogger<JobMapper>>()));
        // No GPU profile yet: the estimator starts unseeded and learns from the first finished jobs (M2b seeds it later).
        services.AddSingleton<IEtaEstimator>(_ => new EtaEstimator());
        services.AddSingleton(CreatePipeline);
    }

    /// <summary>The one place that decides which <see cref="IForgePipeline"/> runs.</summary>
    private static IForgePipeline CreatePipeline(IServiceProvider services)
    {
        var run = services.GetRequiredService<ForgeRunSettings>();
        if (run.Sequential)
        {
            return CreateSequentialPipeline(services);
        }

        // The staged engine (IMPLEMENTATION_GUIDE §11). --sequential above keeps the one-at-a-time fallback for debugging.
        var forge = services.GetRequiredService<ForgeContext>();
        return new ForgePipeline(
            services.GetRequiredService<JobStore>(),
            services.GetRequiredService<TextureProcessor>(),
            services.GetRequiredService<IEtaEstimator>(),
            services.GetRequiredService<PipelineOptions>(),
            services.GetRequiredService<ILogger<ForgePipeline>>())
        {
            NormalStrengthForGroup = group => NormalStrengthFor(forge.Rules, group),
        };
    }

    /// <summary>Gets the normal strength of a rule group by name; 1.0 when the group no longer exists.</summary>
    /// <param name="rules">The project's rules.</param>
    /// <param name="group">The group name stored on the job.</param>
    /// <returns>The strength.</returns>
    internal static double NormalStrengthFor(ForgeRules rules, string group) =>
        rules.Groups.FirstOrDefault(g => string.Equals(g.Name, group, StringComparison.OrdinalIgnoreCase))?.NormalStrength ?? 1.0;

    private static SequentialPipeline CreateSequentialPipeline(IServiceProvider services)
    {
        var run = services.GetRequiredService<ForgeRunSettings>();
        return new SequentialPipeline(
            services.GetRequiredService<JobStore>(),
            services.GetRequiredService<TextureProcessor>(),
            services.GetRequiredService<JobMapper>(),
            services.GetRequiredService<PipelineOptions>(),
            run.Limit,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<SequentialPipeline>>());
    }
}
