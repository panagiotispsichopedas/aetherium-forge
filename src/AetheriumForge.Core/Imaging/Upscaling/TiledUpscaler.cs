using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Upscales images tile by tile with an <see cref="IModelRunner"/> (IMPLEMENTATION_GUIDE §9): bounded video memory,
/// pause and cancel between tiles, and an automatic tile-size reduction when the device runs out of memory.
/// </summary>
/// <remarks>
/// Tiles are read with overlap, the scaled overlap is cropped from each output (or blended linearly when
/// <see cref="TilingOptions.Feather"/> is set) and written into one output buffer. Model calls run on the thread pool and
/// are serialised, so one instance never runs two tiles at once. The instance owns the runner and disposes it.
/// </remarks>
public sealed partial class TiledUpscaler : IUpscaler
{
    private readonly IModelRunner _runner;
    private readonly TilingOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runnerGate = new(1, 1);
    private int _tileSize;
    private int _disposeState;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TiledUpscaler"/> class.
    /// </summary>
    /// <param name="runner">The model runner; this instance takes ownership and disposes it.</param>
    /// <param name="modelName">The model name reported by <see cref="ModelName"/>.</param>
    /// <param name="options">Tiling options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options or the runner scale are out of range.</exception>
    public TiledUpscaler(IModelRunner runner, string modelName, TilingOptions options, ILogger<TiledUpscaler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinTileSize, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TileSize, options.MinTileSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.Overlap, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(runner.Scale, 1, nameof(runner));

        _runner = runner;
        _options = options;
        _logger = logger ?? NullLogger<TiledUpscaler>.Instance;
        _tileSize = options.TileSize;
        ModelName = modelName;
        Scale = runner.Scale;
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public int Scale { get; }

    /// <summary>
    /// Gets the tile size in use. It starts at <see cref="TilingOptions.TileSize"/> and stays reduced after an out-of-memory fallback.
    /// </summary>
    public int CurrentTileSize => Volatile.Read(ref _tileSize);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="rgb"/> does not have three channels.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; no output is returned.</exception>
    /// <exception cref="VideoMemoryExhaustedException">The device is out of memory even at <see cref="TilingOptions.MinTileSize"/>.</exception>
    public async Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgb.Channels != 3)
        {
            throw new ArgumentException($"Expected a three-channel image, got {rgb.Channels} channels.", nameof(rgb));
        }

        while (true)
        {
            var tileSize = CurrentTileSize;
            try
            {
                return await UpscaleWithTileSizeAsync(rgb, tileSize, progress, pause, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && VideoMemoryErrors.IsOutOfMemory(exception))
            {
                if (tileSize <= _options.MinTileSize)
                {
                    LogOutOfMemoryAtMinimum(_logger, exception, ModelName, tileSize, rgb.Width, rgb.Height);
                    throw;
                }

                var reduced = Math.Max(_options.MinTileSize, tileSize / 2);

                // Keep the smaller size for later images; another caller may already have reduced it further.
                Interlocked.CompareExchange(ref _tileSize, reduced, tileSize);
                LogTileSizeReduced(_logger, exception, ModelName, tileSize, CurrentTileSize, rgb.Width, rgb.Height);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Waits for a model call in progress to finish before disposing the runner, so the runner is never disposed under a
    /// running tile. Calls waiting for the gate then fail with <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _runnerGate.Wait();
        try
        {
            _runner.Dispose();
        }
        finally
        {
            // Released, not disposed: queued WaitAsync callers must still wake up, see _disposed and throw. Disposing a
            // SemaphoreSlim under pending waiters would leave them waiting forever, and it owns no unmanaged resource unless
            // AvailableWaitHandle is read, which this class never does.
            _runnerGate.Release();
        }
    }

    private async Task<PlanarImage> UpscaleWithTileSizeAsync(PlanarImage rgb, int tileSize, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        var columns = TileGrid.Split(rgb.Width, tileSize, _options.Overlap);
        var rows = TileGrid.Split(rgb.Height, tileSize, _options.Overlap);
        var total = columns.Length * rows.Length;
        var output = new PlanarImage(3, checked(rgb.Width * Scale), checked(rgb.Height * Scale));
        float[][]? columnWeights = _options.Feather ? Array.ConvertAll(columns, span => TileGrid.FeatherWeights(span, Scale)) : null;
        float[][]? rowWeights = _options.Feather ? Array.ConvertAll(rows, span => TileGrid.FeatherWeights(span, Scale)) : null;

        LogUpscaleStarted(_logger, ModelName, rgb.Width, rgb.Height, tileSize, total);
        progress?.Report(new TileProgress(0, total));

        var done = 0;
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < columns.Length; column++)
            {
                // Safe point: pause and stop take effect between tiles, never inside one.
                await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var xSpan = columns[column];
                var ySpan = rows[row];
                var input = Extract(rgb, xSpan, ySpan);
                var result = await RunTileAsync(input, cancellationToken).ConfigureAwait(false);
                ValidateTileOutput(result, input);

                if (columnWeights is not null && rowWeights is not null)
                {
                    Blend(output, result, xSpan, ySpan, columnWeights[column], rowWeights[row]);
                }
                else
                {
                    Crop(output, result, xSpan, ySpan);
                }

                done++;
                progress?.Report(new TileProgress(done, total));
            }
        }

        Clamp(output.Data);
        return output;
    }

    private async Task<PlanarImage> RunTileAsync(PlanarImage input, CancellationToken cancellationToken)
    {
        // Cancellation is honoured before a tile starts, never during one: ONNX Run cannot be interrupted, so the gate is
        // held until the model call returns. Otherwise a cancelled await would release the gate while Run is still on the
        // device and let a second tile (or Dispose) run concurrently. Cancel latency is at most one tile.
        await _runnerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => _runner.Run(input), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runnerGate.Release();
        }
    }

    private void ValidateTileOutput(PlanarImage result, PlanarImage input)
    {
        if (result.Channels != 3 || result.Width != input.Width * Scale || result.Height != input.Height * Scale)
        {
            throw new InvalidOperationException(
                $"Model '{ModelName}' returned {result.Channels}×{result.Width}×{result.Height} for a {input.Width}×{input.Height} tile; expected 3×{input.Width * Scale}×{input.Height * Scale}.");
        }
    }

    private static PlanarImage Extract(PlanarImage source, TileSpan xSpan, TileSpan ySpan)
    {
        var tile = new PlanarImage(3, xSpan.InputLength, ySpan.InputLength);
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < ySpan.InputLength; y++)
            {
                var from = source.IndexOf(channel, xSpan.InputStart, ySpan.InputStart + y);
                var to = tile.IndexOf(channel, 0, y);
                Array.Copy(source.Data, from, tile.Data, to, xSpan.InputLength);
            }
        }

        return tile;
    }

    private void Crop(PlanarImage output, PlanarImage result, TileSpan xSpan, TileSpan ySpan)
    {
        // The core of the tile starts scale × overlap pixels into the model output.
        var offsetX = xSpan.LeadingOverlap * Scale;
        var offsetY = ySpan.LeadingOverlap * Scale;
        var width = xSpan.CoreLength * Scale;
        var height = ySpan.CoreLength * Scale;
        var targetX = xSpan.CoreStart * Scale;
        var targetY = ySpan.CoreStart * Scale;

        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                var from = result.IndexOf(channel, offsetX, offsetY + y);
                var to = output.IndexOf(channel, targetX, targetY + y);
                Array.Copy(result.Data, from, output.Data, to, width);
            }
        }
    }

    private void Blend(PlanarImage output, PlanarImage result, TileSpan xSpan, TileSpan ySpan, float[] columnWeights, float[] rowWeights)
    {
        var targetX = xSpan.InputStart * Scale;
        var targetY = ySpan.InputStart * Scale;
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < result.Height; y++)
            {
                var rowWeight = rowWeights[y];
                if (rowWeight == 0f)
                {
                    continue;
                }

                var from = result.IndexOf(channel, 0, y);
                var to = output.IndexOf(channel, targetX, targetY + y);
                for (var x = 0; x < result.Width; x++)
                {
                    var weight = rowWeight * columnWeights[x];
                    if (weight != 0f)
                    {
                        output.Data[to + x] += weight * result.Data[from + x];
                    }
                }
            }
        }
    }

    private static void Clamp(float[] data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            data[i] = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Upscaling {Width}×{Height} with {Model}: tile {TileSize}, {Tiles} tiles")]
    private static partial void LogUpscaleStarted(ILogger logger, string model, int width, int height, int tileSize, int tiles);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Out of video memory upscaling {Width}×{Height} with {Model} at tile size {TileSize}; retrying the image with tile size {ReducedTileSize}")]
    private static partial void LogTileSizeReduced(ILogger logger, Exception exception, string model, int tileSize, int reducedTileSize, int width, int height);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Out of video memory upscaling {Width}×{Height} with {Model} at the minimum tile size {TileSize}")]
    private static partial void LogOutOfMemoryAtMinimum(ILogger logger, Exception exception, string model, int tileSize, int width, int height);
}
