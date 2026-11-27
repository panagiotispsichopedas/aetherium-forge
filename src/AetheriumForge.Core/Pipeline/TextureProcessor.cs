using System.Diagnostics;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Processes one texture end to end (IMPLEMENTATION_GUIDE §10): extract → decode → split alpha → upscale →
/// resize → normal renormalisation → merge alpha → BC7 encode → atomic commit.
/// </summary>
/// <remarks>
/// <para>Rules that mirror the reference prototype:</para>
/// <list type="bullet">
/// <item>Alpha is kept only when some pixel is below 255, and never goes through the AI model; it is resized with bicubic.</item>
/// <item>RGB goes through the model, then Lanczos-3 to the exact target size.</item>
/// <item>Normal maps are renormalised after resizing, with the group's normal strength.</item>
/// <item>Parallax/height maps (<see cref="TextureKind.Parallax"/>) skip the model and use Lanczos-3 only (§8).</item>
/// </list>
/// <para>The output file appears only when it is complete: the encoder writes in the work folder and moves the finished
/// file into place, so an interrupted item never leaves a partial texture.</para>
/// </remarks>
public sealed partial class TextureProcessor
{
    private static readonly char[] PathSeparators = ['\\', '/'];

    private readonly ITextureSource _source;
    private readonly ITextureCodec _codec;
    private readonly Func<string, IUpscaler> _upscalers;
    private readonly string _outputRoot;
    private readonly string _workRoot;
    private readonly ILogger<TextureProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextureProcessor"/> class.
    /// </summary>
    /// <param name="source">Provides original texture bytes.</param>
    /// <param name="codec">DDS decoder and BC7 encoder.</param>
    /// <param name="upscalers">Returns the upscaler for a model name (for example <c>UpscalerFactory.Get</c>).</param>
    /// <param name="outputRoot">Mod output folder; textures go to <c>&lt;outputRoot&gt;\textures\...</c>.</param>
    /// <param name="workRoot">Folder for temporary files.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    public TextureProcessor(ITextureSource source, ITextureCodec codec, Func<string, IUpscaler> upscalers, string outputRoot, string workRoot, ILogger<TextureProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(upscalers);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);

        _source = source;
        _codec = codec;
        _upscalers = upscalers;
        _outputRoot = outputRoot;
        _workRoot = workRoot;
        _logger = logger ?? NullLogger<TextureProcessor>.Instance;
    }

    /// <summary>
    /// Gets the output path for a canonical resource path.
    /// </summary>
    /// <param name="canonicalPath">For example <c>textures\clutter\bucket01.dds</c>.</param>
    /// <returns>The full output file path, always inside the output folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="canonicalPath"/> is rooted, has a <c>..</c> segment, or does not resolve to a file inside the output folder.</exception>
    public string GetOutputPath(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        if (Path.IsPathRooted(canonicalPath))
        {
            throw new ArgumentException($"Texture path '{canonicalPath}' must be relative to the output folder.", nameof(canonicalPath));
        }

        foreach (var segment in canonicalPath.Split(PathSeparators))
        {
            if (segment == "..")
            {
                throw new ArgumentException($"Texture path '{canonicalPath}' must not contain '..' segments.", nameof(canonicalPath));
            }
        }

        var root = Path.GetFullPath(_outputRoot);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, canonicalPath));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || fullPath.Length == rootWithSeparator.Length)
        {
            throw new ArgumentException($"Texture path '{canonicalPath}' does not resolve to a file inside the output folder.", nameof(canonicalPath));
        }

        return fullPath;
    }

    /// <summary>
    /// Processes <paramref name="job"/> and commits the result.
    /// </summary>
    /// <param name="job">The texture to process.</param>
    /// <param name="progress">Optional tile progress from the upscaler.</param>
    /// <param name="pause">Pause gate; checked between stages and (by the upscaler) between tiles.</param>
    /// <param name="cancellationToken">Cancels the job; nothing is committed when cancelled.</param>
    /// <returns>The committed output and stage timings.</returns>
    public async Task<TextureResult> ProcessAsync(TextureJob job, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfLessThan(job.TargetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(job.TargetHeight, 1);

        // Validate the destination before reading anything: job.Path must stay inside the output folder.
        var outputPath = GetOutputPath(job.Path);

        var work = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew();
        try
        {
            // 1. Extract
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await _source.ReadAsync(job.Path, job.SourceArchive, cancellationToken).ConfigureAwait(false);
            var sourcePath = Path.Combine(work, "source.dds");
            await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken).ConfigureAwait(false);
            var extract = Lap(clock);

            // 2. Decode
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var decoded = await _codec.DecodeAsync(sourcePath, work, cancellationToken).ConfigureAwait(false);
            var decode = Lap(clock);

            // 3. Split
            var alpha = ImageOps.ExtractAlpha(decoded);
            var rgb = ImageOps.ToPlanarRgb(decoded);

            // 4. Upscale (not for parallax/height maps)
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var useModel = job.Kind != TextureKind.Parallax;
            if (useModel)
            {
                rgb = await _upscalers(job.Model).UpscaleAsync(rgb, progress, pause, cancellationToken).ConfigureAwait(false);
            }

            var upscale = Lap(clock);

            // 5–7. Resize, normals, merge alpha
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var output = await Task.Run(() => PostProcess(job, rgb, alpha), cancellationToken).ConfigureAwait(false);
            var post = Lap(clock);

            // 8–9. Encode and commit (the codec moves the finished file into place)
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            await _codec.EncodeBc7Async(output, outputPath, work, Texconv.IsSrgbFormat(job.SourceDxgiFormat), cancellationToken).ConfigureAwait(false);
            var encode = Lap(clock);

            var timings = new StageTimings(extract, decode, upscale, post, encode);
            LogProcessed(job.Path, decoded.Width, decoded.Height, output.Width, output.Height, timings.Total.TotalSeconds);

            return new TextureResult(outputPath, output.Width, output.Height, alpha is not null, useModel, timings);
        }
        finally
        {
            TryDeleteWorkFolder(work);
        }
    }

    internal static RgbaImage PostProcess(TextureJob job, PlanarImage rgb, PlanarImage? alpha)
    {
        var resized = rgb.Width == job.TargetWidth && rgb.Height == job.TargetHeight
            ? rgb
            : ImageOps.Resize(rgb, job.TargetWidth, job.TargetHeight, ResizeFilter.Lanczos3);

        if (job.Kind == TextureKind.Normal)
        {
            ImageOps.RenormalizeNormals(resized, job.NormalStrength);
        }

        var resizedAlpha = alpha is null ? null : ImageOps.Resize(alpha, job.TargetWidth, job.TargetHeight, ResizeFilter.Bicubic);

        return ImageOps.ToRgba(resized, resizedAlpha);
    }

    private static TimeSpan Lap(Stopwatch clock)
    {
        var elapsed = clock.Elapsed;
        clock.Restart();
        return elapsed;
    }

    private void TryDeleteWorkFolder(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException ex)
        {
            LogWorkCleanupFailed(ex, work);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogWorkCleanupFailed(ex, work);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processed {Path}: {SourceWidth}×{SourceHeight} → {Width}×{Height} in {Seconds:F1} s")]
    private partial void LogProcessed(string path, int sourceWidth, int sourceHeight, int width, int height, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete work folder {Folder}; it is cleaned on the next start")]
    private partial void LogWorkCleanupFailed(Exception exception, string folder);
}
