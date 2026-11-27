using AetheriumForge.App.Services;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// The production <see cref="IPreviewBackend"/>: the open project's archives (<see cref="ArchiveTextureSource"/>), texconv next to the
/// executable (<c>tools\texconv.exe</c>) and the installed models through one lazily created <see cref="UpscalerFactory"/>.
/// </summary>
public sealed class ProjectPreviewBackend : IPreviewBackend, IDisposable
{
    private readonly IProjectSession? _session;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _modelsFolder;
    private readonly Lock _gate = new();
    private ArchiveTextureSource? _source;
    private string? _sourceDataFolder;
    private Texconv? _codec;
    private UpscalerFactory? _upscalers;
    private string _upscalerModels = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectPreviewBackend"/> class.
    /// </summary>
    /// <param name="session">The project session, or <see langword="null"/> when none is registered yet.</param>
    /// <param name="loggerFactory">Logger factory; <see langword="null"/> disables logging.</param>
    /// <param name="modelsFolder">The models folder; defaults to <see cref="ModelCatalog.DefaultModelsFolder"/>.</param>
    public ProjectPreviewBackend(IProjectSession? session, ILoggerFactory? loggerFactory = null, string? modelsFolder = null)
    {
        _session = session;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _modelsFolder = modelsFolder ?? ModelCatalog.DefaultModelsFolder;
    }

    /// <summary>Gets the texconv path the backend uses when it exists.</summary>
    public static string TexconvPath { get; } = Path.Combine(AppContext.BaseDirectory, "tools", "texconv.exe");

    /// <inheritdoc />
    public ITextureSource? Source
    {
        get
        {
            var project = _session?.Current;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (project is null)
                {
                    _source?.Dispose();
                    _source = null;
                    _sourceDataFolder = null;
                    return null;
                }

                if (_source is null || !string.Equals(_sourceDataFolder, project.DataFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _source?.Dispose();
                    _source = new ArchiveTextureSource(project.DataFolder);
                    _sourceDataFolder = project.DataFolder;
                }

                return _source;
            }
        }
    }

    /// <inheritdoc />
    public ITextureCodec? Codec
    {
        get
        {
            lock (_gate)
            {
                if (_codec is null && File.Exists(TexconvPath))
                {
                    _codec = new Texconv(new TexconvOptions { ExecutablePath = TexconvPath }, _loggerFactory.CreateLogger<Texconv>());
                }

                return _codec;
            }
        }
    }

    /// <inheritdoc />
    public string? WorkFolder => DiskCacheFolder is { } folder ? Path.Combine(folder, "tmp") : null;

    /// <inheritdoc />
    public string? DiskCacheFolder => _session?.Current is { } project ? Path.Combine(project.Root, "work", "preview") : null;

    /// <inheritdoc />
    public IReadOnlyList<ModelInfo> Models => ModelCatalog.Discover(_modelsFolder, _loggerFactory.CreateLogger(typeof(ModelCatalog)));

    /// <inheritdoc />
    public IUpscaler GetUpscaler(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var models = Models;
        var names = string.Join('|', models.Select(info => info.Name));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A model downloaded or removed since the factory was made: start a new factory with the current catalogue.
            if (_upscalers is null || _upscalerModels != names)
            {
                _upscalers?.Dispose();
                _upscalers = new UpscalerFactory(models, new OnnxRunnerOptions(), new TilingOptions(), _loggerFactory);
                _upscalerModels = names;
            }

            return _upscalers.Get(model);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source?.Dispose();
            _upscalers?.Dispose();
        }
    }
}
