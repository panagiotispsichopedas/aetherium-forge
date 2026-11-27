using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Textures;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// One sample in the Compare page's thumbnail strip (or search results): a job and its thumbnail.
/// </summary>
public sealed partial class CompareSampleViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CompareSampleViewModel"/> class.
    /// </summary>
    /// <param name="job">The job row.</param>
    public CompareSampleViewModel(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Job = job;
        Kind = Enum.TryParse<TextureKind>(job.Kind, ignoreCase: true, out var kind) ? kind : TextureClassifier.GetKind(job.Path);
    }

    /// <summary>Gets the job row.</summary>
    public JobRecord Job { get; }

    /// <summary>Gets the texture kind.</summary>
    public TextureKind Kind { get; }

    /// <summary>Gets the file name, for the strip label.</summary>
    public string Name => Path.GetFileName(Job.Path);

    /// <summary>Gets the canonical path, for the tooltip.</summary>
    public string FullPath => Job.Path;

    /// <summary>Gets a short "source → target" description.</summary>
    public string SizeText
    {
        get
        {
            var (width, height) = TargetSize;
            return $"{Job.SourceWidth}×{Job.SourceHeight} → {width}×{height} · {Kind}";
        }
    }

    /// <summary>Gets the target size (the row's target on the larger side, aspect kept).</summary>
    public (int Width, int Height) TargetSize =>
        Job.SourceWidth > 0 && Job.SourceHeight > 0 && Job.Target > 0
            ? PreviewSample.FitToMax(Job.SourceWidth, Job.SourceHeight, Job.Target)
            : (Math.Max(1, Job.SourceWidth), Math.Max(1, Job.SourceHeight));

    /// <summary>Gets or sets the thumbnail (the decoded source), or <see langword="null"/> while loading.</summary>
    [ObservableProperty]
    public partial RgbaImage? Thumbnail { get; set; }

    /// <summary>Gets or sets whether the upscaled sample is cached (instant preview).</summary>
    [ObservableProperty]
    public partial bool IsCached { get; set; }

    /// <summary>Builds the preview request for this sample with <paramref name="model"/>.</summary>
    /// <param name="model">The model, or <see langword="null"/> for Lanczos only.</param>
    /// <returns>The request.</returns>
    public PreviewRequest ToRequest(string? model)
    {
        var (width, height) = TargetSize;
        return new PreviewRequest(Job.Path, Job.Archive, Kind, width, height, model);
    }
}
