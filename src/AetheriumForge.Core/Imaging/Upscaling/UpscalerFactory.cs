using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Creates one <see cref="IUpscaler"/> per model on first use and reuses it for the whole run (IMPLEMENTATION_GUIDE §9:
/// one session per model, loaded lazily). Thread-safe. Disposing the factory disposes every upscaler it created.
/// </summary>
public sealed partial class UpscalerFactory : IDisposable
{
    private readonly Dictionary<string, ModelInfo> _models;
    private readonly Dictionary<string, IUpscaler> _upscalers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ModelInfo, IModelRunner> _runnerFactory;
    private readonly TilingOptions _tiling;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpscalerFactory"/> class backed by <see cref="OnnxModelRunner"/>.
    /// </summary>
    /// <param name="models">The available models, usually from <see cref="ModelCatalog.Discover(string)"/>. Names are matched case-insensitively.</param>
    /// <param name="runnerOptions">Execution provider options for every model.</param>
    /// <param name="tiling">Tiling options for every model.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public UpscalerFactory(IReadOnlyList<ModelInfo> models, OnnxRunnerOptions runnerOptions, TilingOptions tiling, ILoggerFactory? loggerFactory = null)
        : this(models, CreateOnnxRunnerFactory(runnerOptions, loggerFactory ?? NullLoggerFactory.Instance), tiling, loggerFactory)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpscalerFactory"/> class with a custom runner factory (tests).
    /// </summary>
    /// <param name="models">The available models.</param>
    /// <param name="runnerFactory">Creates the runner for a model.</param>
    /// <param name="tiling">Tiling options for every model.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    internal UpscalerFactory(IReadOnlyList<ModelInfo> models, Func<ModelInfo, IModelRunner> runnerFactory, TilingOptions tiling, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(runnerFactory);
        ArgumentNullException.ThrowIfNull(tiling);

        _models = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model, nameof(models));
            if (!_models.TryAdd(model.Name, model))
            {
                throw new ArgumentException($"Duplicate model name '{model.Name}'.", nameof(models));
            }
        }

        _runnerFactory = runnerFactory;
        _tiling = tiling;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<UpscalerFactory>();
    }

    /// <summary>
    /// Gets the upscaler for <paramref name="modelName"/>, loading the model on first use.
    /// </summary>
    /// <param name="modelName">The model name (<see cref="ModelInfo.Name"/>).</param>
    /// <returns>The cached upscaler. Do not dispose it; the factory owns it.</returns>
    /// <exception cref="KeyNotFoundException">No model has that name.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    public IUpscaler Get(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_upscalers.TryGetValue(modelName, out var cached))
            {
                return cached;
            }

            if (!_models.TryGetValue(modelName, out var model))
            {
                throw new KeyNotFoundException($"Unknown model '{modelName}'.");
            }

            // Loading a session takes a moment; holding the lock keeps one session per model. A failed load is not cached.
            var runner = _runnerFactory(model);
            TiledUpscaler upscaler;
            try
            {
                if (model.Scale is { } declared && declared != runner.Scale)
                {
                    LogScaleMismatch(_logger, model.Name, declared, runner.Scale);
                }

                upscaler = new TiledUpscaler(runner, model.Name, _tiling, _loggerFactory.CreateLogger<TiledUpscaler>());
            }
            catch
            {
                runner.Dispose();
                throw;
            }

            _upscalers.Add(model.Name, upscaler);
            return upscaler;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IUpscaler[] created;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            created = [.. _upscalers.Values];
            _upscalers.Clear();
        }

        foreach (var upscaler in created)
        {
            upscaler.Dispose();
        }
    }

    private static Func<ModelInfo, IModelRunner> CreateOnnxRunnerFactory(OnnxRunnerOptions runnerOptions, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(runnerOptions);
        return model => OnnxModelRunner.Create(model.Path, runnerOptions, loggerFactory.CreateLogger<OnnxModelRunner>());
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Model {Model} sidecar declares scale ×{DeclaredScale} but the model runs at ×{ActualScale}; using the model scale")]
    private static partial void LogScaleMismatch(ILogger logger, string model, int declaredScale, int actualScale);
}
