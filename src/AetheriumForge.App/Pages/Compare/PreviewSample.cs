using AetheriumForge.Core.Common;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// What to preview: one texture, upscaled with one model to one target size.
/// </summary>
/// <param name="Path">Canonical resource path.</param>
/// <param name="Archive">The archive that provides the original.</param>
/// <param name="Kind">The texture kind.</param>
/// <param name="TargetWidth">Output width.</param>
/// <param name="TargetHeight">Output height.</param>
/// <param name="Model">The model, or <see langword="null"/> for Lanczos only.</param>
public sealed record PreviewRequest(string Path, string Archive, TextureKind Kind, int TargetWidth, int TargetHeight, string? Model)
{
    /// <summary>Gets whether the AI model runs (not for parallax maps or jobs without a model), mirroring the texture processor.</summary>
    public bool UsesModel => Kind != TextureKind.Parallax && !string.IsNullOrWhiteSpace(Model);

    /// <summary>Gets the cache key: path, archive, effective model and target size, case-insensitive.</summary>
    public string CacheKey => string.Join(
        '|',
        GamePath.Normalize(Path),
        Archive.ToLowerInvariant(),
        UsesModel ? Model!.ToLowerInvariant() : "lanczos",
        $"{TargetWidth}x{TargetHeight}");
}

/// <summary>
/// A cached preview: the source and the resized AI, Lanczos and alpha planes the look is rendered from. Immutable once built.
/// </summary>
public sealed class PreviewSample
{
    private readonly Lock _draftGate = new();
    private LookInput? _draft;
    private int _draftMaxSide;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewSample"/> class.
    /// </summary>
    /// <param name="request">The request it was built for.</param>
    /// <param name="source">The decoded source at its own size (the "before" side).</param>
    /// <param name="aiRgb">The AI output resized to the target, or the Lanczos planes when no model ran.</param>
    /// <param name="lanczosRgb">The plain Lanczos upscale of the source to the target.</param>
    /// <param name="alpha">The alpha plane at the target size, or <see langword="null"/>.</param>
    /// <param name="usedModel">Whether the AI model ran.</param>
    public PreviewSample(PreviewRequest request, RgbaImage source, PlanarImage aiRgb, PlanarImage lanczosRgb, PlanarImage? alpha, bool usedModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(aiRgb);
        ArgumentNullException.ThrowIfNull(lanczosRgb);

        Request = request;
        Source = source;
        AiRgb = aiRgb;
        LanczosRgb = lanczosRgb;
        Alpha = alpha;
        UsedModel = usedModel;
        SizeInBytes = source.Pixels.LongLength
            + (aiRgb.Data.LongLength * sizeof(float))
            + (ReferenceEquals(aiRgb, lanczosRgb) ? 0 : lanczosRgb.Data.LongLength * sizeof(float))
            + ((alpha?.Data.LongLength ?? 0) * sizeof(float));
    }

    /// <summary>Gets the request.</summary>
    public PreviewRequest Request { get; }

    /// <summary>Gets the decoded source.</summary>
    public RgbaImage Source { get; }

    /// <summary>Gets the AI planes at the target size.</summary>
    public PlanarImage AiRgb { get; }

    /// <summary>Gets the Lanczos planes at the target size.</summary>
    public PlanarImage LanczosRgb { get; }

    /// <summary>Gets the alpha plane at the target size, or <see langword="null"/>.</summary>
    public PlanarImage? Alpha { get; }

    /// <summary>Gets whether the AI model ran.</summary>
    public bool UsedModel { get; }

    /// <summary>Gets the approximate memory held by the sample, for the LRU cap.</summary>
    public long SizeInBytes { get; }

    /// <summary>Gets the full-resolution look input.</summary>
    public LookInput Input => new(AiRgb, LanczosRgb, Alpha, Request.Kind);

    /// <summary>
    /// Gets a downscaled look input whose larger side is at most <paramref name="maxSide"/> (for live dragging). Built once and reused.
    /// </summary>
    /// <param name="maxSide">The largest side of the draft.</param>
    /// <returns>The draft input, or the full input when the sample is already small enough.</returns>
    public LookInput GetDraftInput(int maxSide)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        if (Math.Max(AiRgb.Width, AiRgb.Height) <= maxSide)
        {
            return Input;
        }

        lock (_draftGate)
        {
            if (_draft is null || _draftMaxSide != maxSide)
            {
                var (width, height) = FitToMax(AiRgb.Width, AiRgb.Height, maxSide);
                var ai = ImageOps.Resize(AiRgb, width, height, ResizeFilter.Bicubic);
                var lanczos = ReferenceEquals(AiRgb, LanczosRgb) ? ai : ImageOps.Resize(LanczosRgb, width, height, ResizeFilter.Bicubic);
                var alpha = Alpha is null ? null : ImageOps.Resize(Alpha, width, height, ResizeFilter.Bicubic);
                _draft = new LookInput(ai, lanczos, alpha, Request.Kind);
                _draftMaxSide = maxSide;
            }

            return _draft;
        }
    }

    /// <summary>Scales a size so its larger side equals <paramref name="maxSide"/>, keeping the aspect ratio.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="maxSide">The larger side of the result.</param>
    /// <returns>The scaled size; each side at least 1.</returns>
    public static (int Width, int Height) FitToMax(int width, int height, int maxSide) =>
        width >= height
            ? (maxSide, Math.Max(1, (int)Math.Round((double)height * maxSide / width)))
            : (Math.Max(1, (int)Math.Round((double)width * maxSide / height)), maxSide);
}

/// <summary>
/// The outcome of loading a preview sample.
/// </summary>
/// <param name="Sample">The sample, or <see langword="null"/> when it is not available now.</param>
/// <param name="UnavailableReason">Why the sample is not available (for example a pipeline run holds the GPU), or <see langword="null"/>.</param>
public sealed record PreviewLoadResult(PreviewSample? Sample, string? UnavailableReason)
{
    /// <summary>Gets whether a sample is available.</summary>
    public bool IsAvailable => Sample is not null;
}
