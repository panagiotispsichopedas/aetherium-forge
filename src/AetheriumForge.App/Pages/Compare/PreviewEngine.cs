using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// A request to render a look onto a cached sample.
/// </summary>
/// <param name="Sample">The cached sample.</param>
/// <param name="Look">The look.</param>
/// <param name="Draft"><see langword="true"/> for a fast downscaled render while a slider is dragged.</param>
public sealed record PreviewRenderRequest(PreviewSample Sample, LookSettings Look, bool Draft);

/// <summary>
/// A finished render.
/// </summary>
/// <param name="Sample">The sample it was rendered from.</param>
/// <param name="Look">The look it shows.</param>
/// <param name="Image">The rendered RGBA image (target size, or draft size).</param>
/// <param name="IsDraft">Whether it is a downscaled draft.</param>
public sealed record PreviewRender(PreviewSample Sample, LookSettings Look, RgbaImage Image, bool IsDraft);

/// <summary>
/// The Compare page's preview engine. It builds a sample once per texture and model (read → decode → GPU upscale → resize, plus the
/// Lanczos-only version and the alpha plane), keeps it in an LRU memory cache (about 1.5 GB) and optionally on disk, and renders looks
/// from the cached planes in the background, so slider changes never re-run the AI.
/// </summary>
/// <remarks>
/// <para>GPU upscaling is disabled while a pipeline run is active (any state other than <see cref="PipelineState.Idle"/>): only cached
/// samples are shown then. Upscales run one at a time.</para>
/// <para>Renders go through a <see cref="LatestRenderScheduler{TRequest, TResult}"/>: stale renders are cancelled and intermediate slider
/// values are skipped.</para>
/// </remarks>
public sealed partial class PreviewEngine : IDisposable
{
    /// <summary>The largest side of a draft render.</summary>
    public const int DefaultDraftMaxSide = 768;

    /// <summary>The reason shown when a run holds the GPU.</summary>
    public const string GpuBusyReason = "A run is using the GPU. Only cached previews are shown until it ends.";

    /// <summary>The reason shown when no project is open.</summary>
    public const string NoProjectReason = "Open a project to preview textures.";

    private const int SourceCacheCount = 48;
    private const int DxgiR8G8B8A8Unorm = 28;
    private const int DxgiR8G8B8A8UnormSrgb = 29;

    private readonly IPreviewBackend _backend;
    private readonly Func<PipelineState> _runnerState;
    private readonly Func<LookInput, LookSettings, RgbaImage> _renderer;
    private readonly PreviewMemoryCache _memory;
    private readonly SemaphoreSlim _gpuGate = new(1, 1);
    private readonly LatestRenderScheduler<PreviewRenderRequest, PreviewRender> _renders;
    private readonly ILogger<PreviewEngine> _logger;
    private readonly Lock _sourceGate = new();
    private readonly LinkedList<(string Key, RgbaImage Image)> _sources = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewEngine"/> class.
    /// </summary>
    /// <param name="backend">The texture source, codec and models.</param>
    /// <param name="runnerState">Returns the pipeline state; GPU upscaling runs only while it is <see cref="PipelineState.Idle"/>.</param>
    /// <param name="memoryCapBytes">The LRU memory cap.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    public PreviewEngine(IPreviewBackend backend, Func<PipelineState> runnerState, long memoryCapBytes = PreviewMemoryCache.DefaultCapacityBytes, ILogger<PreviewEngine>? logger = null)
        : this(backend, runnerState, memoryCapBytes, LookRenderer.Render, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewEngine"/> class with a custom look renderer (tests).
    /// </summary>
    /// <param name="backend">The texture source, codec and models.</param>
    /// <param name="runnerState">Returns the pipeline state.</param>
    /// <param name="memoryCapBytes">The LRU memory cap.</param>
    /// <param name="renderer">Renders a look (normally <see cref="LookRenderer.Render"/>).</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    internal PreviewEngine(IPreviewBackend backend, Func<PipelineState> runnerState, long memoryCapBytes, Func<LookInput, LookSettings, RgbaImage> renderer, ILogger<PreviewEngine>? logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(runnerState);
        ArgumentNullException.ThrowIfNull(renderer);

        _backend = backend;
        _runnerState = runnerState;
        _renderer = renderer;
        _memory = new PreviewMemoryCache(memoryCapBytes);
        _renders = new LatestRenderScheduler<PreviewRenderRequest, PreviewRender>(RenderAsync);
        _logger = logger ?? NullLogger<PreviewEngine>.Instance;
    }

    /// <summary>Gets or sets the largest side of a draft render.</summary>
    public int DraftMaxSide { get; set; } = DefaultDraftMaxSide;

    /// <summary>Gets the memory cache (for diagnostics and tests).</summary>
    public PreviewMemoryCache MemoryCache => _memory;

    /// <summary>Gets the installed models.</summary>
    public IReadOnlyList<ModelInfo> Models => _backend.Models;

    /// <summary>Gets whether GPU upscaling is allowed now (no pipeline run is active).</summary>
    public bool IsUpscalingAvailable => _runnerState() == PipelineState.Idle;

    /// <summary>Gets the number of samples built from scratch (decode + upscale), for diagnostics and tests.</summary>
    public int BuiltSamples { get; private set; }

    /// <summary>Gets whether <paramref name="request"/>'s sample is in the memory cache.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when cached in memory.</returns>
    public bool IsCached(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _memory.Contains(request.CacheKey);
    }

    /// <summary>
    /// Gets the sample for <paramref name="request"/>: from memory, else from the disk cache, else built (GPU upscale) when allowed.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="progress">Optional tile progress of the upscale.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The sample, or the reason it is not available now.</returns>
    public async Task<PreviewLoadResult> GetSampleAsync(PreviewRequest request, IProgress<TileProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_memory.TryGet(request.CacheKey, out var cached))
        {
            return new PreviewLoadResult(cached, null);
        }

        if (await TryReadDiskAsync(request, cancellationToken).ConfigureAwait(false) is { } fromDisk)
        {
            _memory.Add(fromDisk);
            return new PreviewLoadResult(fromDisk, null);
        }

        if (_backend.Source is null)
        {
            return new PreviewLoadResult(null, NoProjectReason);
        }

        if (request.UsesModel && !IsUpscalingAvailable)
        {
            return new PreviewLoadResult(null, GpuBusyReason);
        }

        await _gpuGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have built it while this one waited for the gate.
            if (_memory.TryGet(request.CacheKey, out cached))
            {
                return new PreviewLoadResult(cached, null);
            }

            if (request.UsesModel && !IsUpscalingAvailable)
            {
                return new PreviewLoadResult(null, GpuBusyReason);
            }

            var sample = await BuildAsync(request, progress, cancellationToken).ConfigureAwait(false);
            BuiltSamples++;
            foreach (var evicted in _memory.Add(sample))
            {
                LogEvicted(evicted);
            }

            await TryWriteDiskAsync(sample, cancellationToken).ConfigureAwait(false);
            return new PreviewLoadResult(sample, null);
        }
        finally
        {
            _gpuGate.Release();
        }
    }

    /// <summary>
    /// Decodes the source of a texture (no upscale; allowed during a run). Recent sources are cached by count.
    /// </summary>
    /// <param name="path">Canonical path.</param>
    /// <param name="archive">The archive.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The decoded source.</returns>
    /// <exception cref="InvalidOperationException">No project is open, or texconv is missing for a compressed texture.</exception>
    public async Task<RgbaImage> GetSourceAsync(string path, string archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(archive);

        var key = $"{path.ToLowerInvariant()}|{archive.ToLowerInvariant()}";
        lock (_sourceGate)
        {
            foreach (var entry in _sources)
            {
                if (entry.Key == key)
                {
                    return entry.Image;
                }
            }
        }

        var image = await DecodeAsync(path, archive, cancellationToken).ConfigureAwait(false);
        lock (_sourceGate)
        {
            _sources.AddFirst((key, image));
            while (_sources.Count > SourceCacheCount)
            {
                _sources.RemoveLast();
            }
        }

        return image;
    }

    /// <summary>
    /// Renders <paramref name="look"/> onto <paramref name="sample"/> in the background, superseding any pending or running render.
    /// </summary>
    /// <param name="sample">The cached sample.</param>
    /// <param name="look">The look.</param>
    /// <param name="draft"><see langword="true"/> for a downscaled draft (while dragging).</param>
    /// <returns>The render, or a superseded outcome when a newer request arrived first.</returns>
    public Task<RenderOutcome<PreviewRender>> SubmitRenderAsync(PreviewSample sample, LookSettings look, bool draft)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(look);
        return _renders.SubmitAsync(new PreviewRenderRequest(sample, look, draft));
    }

    /// <summary>Cancels any pending or running render.</summary>
    public void CancelRenders() => _renders.CancelAll();

    /// <summary>Drops every cached sample and source from memory (the disk cache stays).</summary>
    public void ClearMemory()
    {
        _memory.Clear();
        lock (_sourceGate)
        {
            _sources.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renders.CancelAll();
        _gpuGate.Dispose();
    }

    /// <summary>Scales the sharpen radius of a draft look so the draft looks like the full render, only smaller.</summary>
    /// <param name="look">The look.</param>
    /// <param name="scale">Draft size / full size.</param>
    /// <returns>The adjusted look.</returns>
    internal static LookSettings ScaleForDraft(LookSettings look, double scale) =>
        scale >= 1.0 || look.Sharpen.Amount <= 0.0
            ? look
            : look with { Sharpen = look.Sharpen with { Radius = Math.Max(0.1, look.Sharpen.Radius * scale) } };

    private Task<PreviewRender> RenderAsync(PreviewRenderRequest request, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = request.Draft ? request.Sample.GetDraftInput(DraftMaxSide) : request.Sample.Input;
                var scale = (double)input.AiRgb.Width / request.Sample.AiRgb.Width;
                cancellationToken.ThrowIfCancellationRequested();
                var image = _renderer(input, ScaleForDraft(request.Look, scale));
                cancellationToken.ThrowIfCancellationRequested();
                return new PreviewRender(request.Sample, request.Look, image, request.Draft && scale < 1.0);
            },
            cancellationToken);

    private async Task<PreviewSample> BuildAsync(PreviewRequest request, IProgress<TileProgress>? progress, CancellationToken cancellationToken)
    {
        var decoded = await DecodeAsync(request.Path, request.Archive, cancellationToken).ConfigureAwait(false);
        var (alpha, rgb) = await Task.Run(() => (ImageOps.ExtractAlpha(decoded), ImageOps.ToPlanarRgb(decoded)), cancellationToken).ConfigureAwait(false);

        var lanczos = await Task.Run(() => ResizeTo(rgb, request, ResizeFilter.Lanczos3), cancellationToken).ConfigureAwait(false);
        var ai = lanczos;
        var usedModel = false;
        if (request.UsesModel)
        {
            var upscaled = await _backend.GetUpscaler(request.Model!).UpscaleAsync(rgb, progress, PauseToken.None, cancellationToken).ConfigureAwait(false);
            ai = await Task.Run(() => ResizeTo(upscaled, request, ResizeFilter.Lanczos3), cancellationToken).ConfigureAwait(false);
            usedModel = true;
        }

        var resizedAlpha = alpha is null ? null : await Task.Run(() => ResizeTo(alpha, request, ResizeFilter.Bicubic), cancellationToken).ConfigureAwait(false);
        LogBuilt(request.Path, request.UsesModel ? request.Model! : "lanczos", request.TargetWidth, request.TargetHeight);
        return new PreviewSample(request, decoded, ai, lanczos, resizedAlpha, usedModel);
    }

    private static PlanarImage ResizeTo(PlanarImage image, PreviewRequest request, ResizeFilter filter) =>
        image.Width == request.TargetWidth && image.Height == request.TargetHeight
            ? image
            : ImageOps.Resize(image, request.TargetWidth, request.TargetHeight, filter);

    private async Task<RgbaImage> DecodeAsync(string path, string archive, CancellationToken cancellationToken)
    {
        var source = _backend.Source ?? throw new InvalidOperationException(NoProjectReason);
        var bytes = await source.ReadAsync(path, archive, cancellationToken).ConfigureAwait(false);
        if (DdsHeader.TryParse(bytes, out var header) && header.DxgiFormat is DxgiR8G8B8A8Unorm or DxgiR8G8B8A8UnormSrgb)
        {
            return RawDds.Read(bytes);
        }

        var codec = _backend.Codec ?? throw new InvalidOperationException("texconv is not available; fetch it with tools\\fetch-tools.ps1 to preview compressed textures.");
        var work = Path.Combine(_backend.WorkFolder ?? Path.GetTempPath(), "preview-decode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var sourcePath = Path.Combine(work, "source.dds");
            await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken).ConfigureAwait(false);
            return await codec.DecodeAsync(sourcePath, work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    private async Task<PreviewSample?> TryReadDiskAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        if (_backend.DiskCacheFolder is not { } folder)
        {
            return null;
        }

        try
        {
            return await new PreviewDiskCache(folder).TryReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // A broken cache file is rebuilt; it is never fatal.
            LogDiskCacheFailed(ex, request.Path);
            return null;
        }
    }

    private async Task TryWriteDiskAsync(PreviewSample sample, CancellationToken cancellationToken)
    {
        if (_backend.DiskCacheFolder is not { } folder)
        {
            return;
        }

        try
        {
            await new PreviewDiskCache(folder).WriteAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDiskCacheFailed(ex, sample.Request.Path);
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(ex, folder);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview built for {Path} with {Model} at {Width}×{Height}")]
    private partial void LogBuilt(string path, string model, int width, int height);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview cache evicted {Key}")]
    private partial void LogEvicted(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preview disk cache failed for {Path}; continuing without it")]
    private partial void LogDiskCacheFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete preview temp folder {Folder}")]
    private partial void LogCleanupFailed(Exception exception, string folder);
}
