using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// A texture between pipeline stages: its job, its private work folder and the image data of the current stage.
/// Created by <see cref="TextureProcessor.DecodeAsync"/>; disposing it deletes the work folder (safe to call more than once).
/// </summary>
/// <remarks>Not thread-safe: exactly one stage owns the texture at a time.</remarks>
internal sealed class DecodedTexture : IDisposable
{
    private readonly Action<string> _deleteWorkFolder;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DecodedTexture"/> class.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="outputPath">Validated final output path.</param>
    /// <param name="workFolder">Private work folder (already created).</param>
    /// <param name="deleteWorkFolder">Deletes the work folder; must not throw.</param>
    public DecodedTexture(TextureJob job, string outputPath, string workFolder, Action<string> deleteWorkFolder)
    {
        Job = job;
        OutputPath = outputPath;
        WorkFolder = workFolder;
        _deleteWorkFolder = deleteWorkFolder;
    }

    /// <summary>Gets the job.</summary>
    public TextureJob Job { get; }

    /// <summary>Gets the final output path inside the output folder.</summary>
    public string OutputPath { get; }

    /// <summary>Gets the texture's private work folder.</summary>
    public string WorkFolder { get; }

    /// <summary>Gets or sets the source DXGI format (from the job, else from the DDS header), used to keep sRGB tagging.</summary>
    public int? SourceDxgiFormat { get; set; }

    /// <summary>Gets or sets the decoded source width.</summary>
    public int SourceWidth { get; set; }

    /// <summary>Gets or sets the decoded source height.</summary>
    public int SourceHeight { get; set; }

    /// <summary>Gets or sets the RGB planes (decoded, then upscaled); <see langword="null"/> after post-processing.</summary>
    public PlanarImage? Rgb { get; set; }

    /// <summary>Gets or sets the kept alpha plane; <see langword="null"/> when the source is opaque or after post-processing.</summary>
    public PlanarImage? Alpha { get; set; }

    /// <summary>Gets or sets a value indicating whether the source alpha channel was kept.</summary>
    public bool HadAlpha { get; set; }

    /// <summary>Gets or sets a value indicating whether the AI model ran.</summary>
    public bool UsedModel { get; set; }

    /// <summary>Gets or sets the final image (after post-processing).</summary>
    public RgbaImage? Output { get; set; }

    /// <summary>Gets or sets the extract time.</summary>
    public TimeSpan Extract { get; set; }

    /// <summary>Gets or sets the decode time.</summary>
    public TimeSpan Decode { get; set; }

    /// <summary>Gets or sets the upscale time.</summary>
    public TimeSpan Upscale { get; set; }

    /// <summary>Gets or sets the post-process time.</summary>
    public TimeSpan PostProcess { get; set; }

    /// <summary>Gets or sets the encode time.</summary>
    public TimeSpan Encode { get; set; }

    /// <summary>Gets the source size in megapixels.</summary>
    public double SourceMegapixels => SourceWidth * (double)SourceHeight / 1_000_000d;

    /// <summary>Releases the image data and deletes the work folder.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Rgb = null;
        Alpha = null;
        Output = null;
        _deleteWorkFolder(WorkFolder);
    }
}
