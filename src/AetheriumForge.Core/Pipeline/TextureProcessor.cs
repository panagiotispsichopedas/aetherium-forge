using System.Diagnostics;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Rules;
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
/// <item>The group's look (§8, <see cref="TextureJob.Look"/>) is applied by <see cref="LookRenderer"/> after resizing; a Lanczos upscale of the
/// source is computed only when the look's AI strength is below 1. A neutral look gives exactly the M2 output.</item>
/// <item>Parallax/height maps (<see cref="TextureKind.Parallax"/>) and jobs without a model skip the model and use Lanczos-3 only (§8).</item>
/// </list>
/// <para>The output file appears only when it is complete: the encoder writes the finished file into the hidden staging folder
/// <c>&lt;outputRoot&gt;\.aetherium-tmp\</c> (<see cref="StagingFolderName"/>), which is on the output's own volume, and a
/// same-volume move then commits it atomically, wherever the work folder lives. An interrupted item never leaves a partial
/// texture under <c>textures\</c>. The staging folder is wiped when the pipeline starts and must never be packaged (the M6
/// packager reads only <c>textures\</c>).</para>
/// <para>The work is split into stage methods (decode, upscale, post-process, encode) that the staged pipeline (§11) calls
/// from different workers; <see cref="ProcessAsync"/> composes them for single-texture use.</para>
/// </remarks>
public sealed partial class TextureProcessor
{
    /// <summary>Name of the hidden staging folder inside the output folder; never part of a packaged mod.</summary>
    public const string StagingFolderName = ".aetherium-tmp";

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

    /// <summary>Gets the mod output folder.</summary>
    internal string OutputRoot => _outputRoot;

    /// <summary>Gets the folder for temporary files.</summary>
    internal string WorkRoot => _workRoot;

    /// <summary>Gets the hidden staging folder on the output volume (<c>&lt;outputRoot&gt;\.aetherium-tmp</c>).</summary>
    internal string StagingRoot => Path.Combine(_outputRoot, StagingFolderName);

    /// <summary>
    /// Gets the output path for a canonical resource path.
    /// </summary>
    /// <param name="canonicalPath">For example <c>textures\clutter\bucket01.dds</c>.</param>
    /// <returns>The full output file path, always inside the output folder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="canonicalPath"/> is rooted, has a <c>..</c> segment, points into the staging folder, or does not resolve to a file inside the output folder.
    /// </exception>
    public string GetOutputPath(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        if (Path.IsPathRooted(canonicalPath))
        {
            throw new ArgumentException($"Texture path '{canonicalPath}' must be relative to the output folder.", nameof(canonicalPath));
        }

        var segments = canonicalPath.Split(PathSeparators);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new ArgumentException($"Texture path '{canonicalPath}' must not contain '..' segments.", nameof(canonicalPath));
            }
        }

        if (string.Equals(segments[0], StagingFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Texture path '{canonicalPath}' must not point into the staging folder.", nameof(canonicalPath));
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
    /// Processes <paramref name="job"/> and commits the result. Composes the decode, upscale, post-process and encode stages.
    /// </summary>
    /// <param name="job">The texture to process.</param>
    /// <param name="progress">Optional tile progress from the upscaler.</param>
    /// <param name="pause">Pause gate; checked between stages and (by the upscaler) between tiles.</param>
    /// <param name="cancellationToken">Cancels the job; nothing is committed when cancelled.</param>
    /// <returns>The committed output and stage timings.</returns>
    public async Task<TextureResult> ProcessAsync(TextureJob job, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        using var texture = await DecodeAsync(job, pause, cancellationToken).ConfigureAwait(false);

        await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        await UpscaleAsync(texture, progress, pause, cancellationToken).ConfigureAwait(false);

        await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => PostProcess(texture), cancellationToken).ConfigureAwait(false);

        await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        return await EncodeAsync(texture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets whether <paramref name="job"/> runs through the AI model (not for parallax/height maps or jobs without a model).</summary>
    /// <param name="job">The job.</param>
    /// <returns><see langword="true"/> when the model runs.</returns>
    internal static bool UsesModel(TextureJob job) => job.Kind != TextureKind.Parallax && !string.IsNullOrEmpty(job.Model);

    /// <summary>
    /// Stages 1–3: extracts the source into a new work folder, decodes it and splits RGB from alpha (CPU).
    /// </summary>
    /// <param name="job">The texture to process.</param>
    /// <param name="pause">Pause gate, checked before extract and before decode.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The decoded texture. The caller owns it; disposing it deletes its work folder.</returns>
    /// <exception cref="ArgumentException"><see cref="TextureJob.Path"/> escapes the output folder (nothing is read).</exception>
    /// <remarks>On any failure, cancellation included, the work folder is deleted before the exception propagates.</remarks>
    internal async Task<DecodedTexture> DecodeAsync(TextureJob job, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfLessThan(job.TargetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(job.TargetHeight, 1);

        // Validate the destination before reading anything: job.Path must stay inside the output folder.
        var outputPath = GetOutputPath(job.Path);
        cancellationToken.ThrowIfCancellationRequested();

        var work = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var texture = new DecodedTexture(job, outputPath, work, TryDeletePath);
        try
        {
            // 1. Extract
            var clock = Stopwatch.StartNew();
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await _source.ReadAsync(job.Path, job.SourceArchive, cancellationToken).ConfigureAwait(false);
            var sourcePath = Path.Combine(work, "source.dds");
            await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken).ConfigureAwait(false);
            texture.SourceDxgiFormat = job.SourceDxgiFormat ?? (DdsHeader.TryParse(bytes, out var header) ? header.DxgiFormat : null);
            texture.Extract = Lap(clock);

            // 2. Decode
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var decoded = await _codec.DecodeAsync(sourcePath, work, cancellationToken).ConfigureAwait(false);

            // 3. Split
            texture.SourceWidth = decoded.Width;
            texture.SourceHeight = decoded.Height;
            texture.Alpha = ImageOps.ExtractAlpha(decoded);
            texture.HadAlpha = texture.Alpha is not null;
            texture.Rgb = ImageOps.ToPlanarRgb(decoded);
            texture.Decode = Lap(clock);

            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stage 4: upscales the RGB planes with the job's model (GPU). Does nothing when <see cref="UsesModel"/> is false.
    /// </summary>
    /// <param name="texture">The decoded texture; its RGB planes are replaced by the upscaled planes.</param>
    /// <param name="progress">Optional tile progress.</param>
    /// <param name="pause">Pause gate, checked by the upscaler between tiles.</param>
    /// <param name="cancellationToken">Cancels between tiles.</param>
    /// <returns>A task that completes when the planes are upscaled.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="texture"/> has no RGB planes (not decoded, or already post-processed).</exception>
    internal async Task UpscaleAsync(DecodedTexture texture, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var rgb = texture.Rgb ?? throw new InvalidOperationException("The texture has no RGB planes to upscale.");
        if (!UsesModel(texture.Job))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        texture.SourceRgb = LookRenderer.NeedsLanczos(texture.Job.EffectiveLook) ? rgb : null;
        texture.Rgb = await _upscalers(texture.Job.Model).UpscaleAsync(rgb, progress, pause, cancellationToken).ConfigureAwait(false);
        texture.UsedModel = true;
        texture.Upscale = clock.Elapsed;
    }

    /// <summary>
    /// Stages 5–7: resizes to the target size, applies the look (normal renormalisation included) and merges alpha (CPU).
    /// </summary>
    /// <param name="texture">The texture; its planes are released and <see cref="DecodedTexture.Output"/> is set.</param>
    /// <exception cref="InvalidOperationException"><paramref name="texture"/> has no RGB planes.</exception>
    internal static void PostProcess(DecodedTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var rgb = texture.Rgb ?? throw new InvalidOperationException("The texture has no RGB planes to post-process.");

        var clock = Stopwatch.StartNew();
        texture.Output = PostProcess(texture.Job, rgb, texture.Alpha, texture.SourceRgb);
        texture.Rgb = null;
        texture.Alpha = null;
        texture.SourceRgb = null;
        texture.PostProcess = clock.Elapsed;
    }

    /// <summary>
    /// Stages 8–9: encodes BC7 (temporary files in the texture's work folder) into the staging folder on the output volume, then
    /// atomically moves the finished file into place.
    /// </summary>
    /// <param name="texture">The post-processed texture. The caller still owns it and disposes it afterwards.</param>
    /// <param name="cancellationToken">Cancels the encode; nothing is committed when cancelled.</param>
    /// <returns>The committed output and stage timings.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="texture"/> has not been post-processed.</exception>
    internal async Task<TextureResult> EncodeAsync(DecodedTexture texture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var output = texture.Output ?? throw new InvalidOperationException("The texture has not been post-processed.");

        var clock = Stopwatch.StartNew();
        var staging = StagingRoot;
        if (!Directory.Exists(staging))
        {
            var created = Directory.CreateDirectory(staging);
            created.Attributes |= FileAttributes.Hidden;
        }

        // The codec may cross volumes into the staging file (a copy, not atomic); only the final same-volume move commits.
        var staged = Path.Combine(staging, $"{Path.GetFileName(texture.WorkFolder)}.dds");
        texture.StagingPath = staged;
        await _codec.EncodeBc7Async(output, staged, texture.WorkFolder, Texconv.IsSrgbFormat(texture.SourceDxgiFormat), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var targetFolder = Path.GetDirectoryName(texture.OutputPath);
        if (!string.IsNullOrEmpty(targetFolder))
        {
            Directory.CreateDirectory(targetFolder);
        }

        File.Move(staged, texture.OutputPath, overwrite: true);
        texture.StagingPath = null;
        texture.Encode = clock.Elapsed;

        var timings = new StageTimings(texture.Extract, texture.Decode, texture.Upscale, texture.PostProcess, texture.Encode);
        LogProcessed(texture.Job.Path, texture.SourceWidth, texture.SourceHeight, output.Width, output.Height, timings.Total.TotalSeconds);

        return new TextureResult(texture.OutputPath, output.Width, output.Height, texture.HadAlpha, texture.UsedModel, timings);
    }

    /// <summary>
    /// Resizes the model output (and alpha) to the job's target size and renders the job's look.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="rgb">The model output (or the source RGB when no model ran).</param>
    /// <param name="alpha">The source alpha, or <see langword="null"/>.</param>
    /// <param name="sourceRgb">The source RGB for the AI-strength blend, or <see langword="null"/> when the look does not blend.</param>
    /// <returns>The RGBA image to encode.</returns>
    internal static RgbaImage PostProcess(TextureJob job, PlanarImage rgb, PlanarImage? alpha, PlanarImage? sourceRgb = null)
    {
        LookSettings look = job.EffectiveLook;
        var resized = ResizeTo(rgb, job.TargetWidth, job.TargetHeight, ResizeFilter.Lanczos3);
        var lanczos = sourceRgb is not null && LookRenderer.NeedsLanczos(look)
            ? ResizeTo(sourceRgb, job.TargetWidth, job.TargetHeight, ResizeFilter.Lanczos3)
            : resized;
        var resizedAlpha = alpha is null ? null : ImageOps.Resize(alpha, job.TargetWidth, job.TargetHeight, ResizeFilter.Bicubic);

        return LookRenderer.Render(new LookInput(resized, lanczos, resizedAlpha, job.Kind), look);
    }

    private static PlanarImage ResizeTo(PlanarImage image, int width, int height, ResizeFilter filter) =>
        image.Width == width && image.Height == height ? image : ImageOps.Resize(image, width, height, filter);

    private static TimeSpan Lap(Stopwatch clock)
    {
        var elapsed = clock.Elapsed;
        clock.Restart();
        return elapsed;
    }

    private void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            LogWorkCleanupFailed(ex, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogWorkCleanupFailed(ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processed {Path}: {SourceWidth}×{SourceHeight} → {Width}×{Height} in {Seconds:F1} s")]
    private partial void LogProcessed(string path, int sourceWidth, int sourceHeight, int width, int height, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete temporary {Path}; it is cleaned on the next start")]
    private partial void LogWorkCleanupFailed(Exception exception, string path);
}
