using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Services;

/// <summary>
/// The real <see cref="IForgeRunBackend"/>: the model catalog, texconv lookup, the GPU profile store, and the per-run services wired like the
/// CLI's composition root (ArchiveTextureSource, Texconv with the process tracker, one UpscalerFactory per device and tile, TextureProcessor,
/// a seeded EtaEstimator and ForgePipeline).
/// </summary>
/// <param name="profiles">The GPU profile store.</param>
/// <param name="processes">Tracks texconv processes so the app can kill them on shutdown.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="modelsFolder">The models folder; defaults to <see cref="ModelCatalog.DefaultModelsFolder"/>.</param>
internal sealed class ForgeRunBackend(GpuProfileStore profiles, ChildProcessTracker processes, ILoggerFactory loggerFactory, string? modelsFolder = null) : IForgeRunBackend
{
    /// <inheritdoc />
    public string ModelsFolder { get; } = Path.GetFullPath(modelsFolder ?? ModelCatalog.DefaultModelsFolder);

    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> DiscoverModels() => ModelCatalog.Discover(ModelsFolder, loggerFactory.CreateLogger(nameof(ModelCatalog)));

    /// <inheritdoc />
    public string FindTexconv() => ForgeRunPlanning.FindTexconv(AppContext.BaseDirectory, File.Exists);

    /// <inheritdoc />
    public Task<GpuProfile?> FindProfileAsync(GpuAdapterInfo adapter, string family, CancellationToken cancellationToken) =>
        profiles.FindAsync(adapter, family, cancellationToken);

    /// <inheritdoc />
    public IForgeRunResources CreateRun(ForgeRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var resources = new Resources();
        try
        {
            var source = new ArchiveTextureSource(plan.Project.DataFolder);
            resources.Add(source);
            var codec = new Texconv(
                new TexconvOptions { ExecutablePath = plan.TexconvPath, GpuAdapter = plan.TexconvDevice, ProcessTracker = processes },
                loggerFactory.CreateLogger<Texconv>());

            // One factory per (device, tile): GPU sessions are created lazily on first use and released when the run ends.
            var factories = new Dictionary<(int Device, int Tile), UpscalerFactory>();
            UpscalerFactory FactoryFor(int device, int tile)
            {
                if (!factories.TryGetValue((device, tile), out var factory))
                {
                    factory = new UpscalerFactory(plan.InstalledModels, new OnnxRunnerOptions { DeviceId = device }, new TilingOptions { TileSize = tile }, loggerFactory);
                    factories.Add((device, tile), factory);
                    resources.Add(factory);
                }

                return factory;
            }

            var lookup = new Dictionary<string, (UpscalerFactory Factory, string Variant)>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in plan.Models.Values)
            {
                lookup[model.Model] = (FactoryFor(model.DeviceId, model.TileSize), model.Variant);
            }

            var fallback = FactoryFor(plan.DefaultDeviceId, ForgeRunPlanning.DefaultTileSize);
            var processor = new TextureProcessor(
                source,
                codec,
                model => lookup.TryGetValue(model, out var entry) ? entry.Factory.Get(entry.Variant) : fallback.Get(model),
                plan.Options.OutputRoot,
                plan.Options.WorkRoot,
                loggerFactory.CreateLogger<TextureProcessor>());

            var eta = new EtaEstimator();
            foreach (var model in plan.Models.Values)
            {
                if (model.SecondsPerMegapixel is { } seed && seed > 0)
                {
                    eta.Seed(model.Model, seed);
                }
            }

            var rules = plan.Project.Rules;
            resources.Pipeline = new ForgePipeline(plan.Store, processor, eta, plan.Options, loggerFactory.CreateLogger<ForgePipeline>())
            {
                NormalStrengthForGroup = group => NormalStrengthFor(rules, group),
            };
            return resources;
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    /// <summary>Gets the normal strength of a rule group by name; 1.0 when the group no longer exists.</summary>
    /// <param name="rules">The project's rules.</param>
    /// <param name="group">The group name stored on the job.</param>
    /// <returns>The strength.</returns>
    internal static double NormalStrengthFor(ForgeRules rules, string group) =>
        rules.Groups.FirstOrDefault(g => string.Equals(g.Name, group, StringComparison.OrdinalIgnoreCase))?.NormalStrength ?? 1.0;

    private sealed class Resources : IForgeRunResources
    {
        private readonly List<IDisposable> _owned = [];

        public IForgePipeline Pipeline { get; set; } = null!;

        public void Add(IDisposable disposable) => _owned.Add(disposable);

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                _owned[i].Dispose();
            }

            _owned.Clear();
        }
    }
}
