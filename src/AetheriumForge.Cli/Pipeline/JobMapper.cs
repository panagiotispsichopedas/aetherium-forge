using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Cli.Pipeline;

/// <summary>
/// Turns a stored <see cref="JobRecord"/> into a <see cref="TextureJob"/>. The job row keeps only the target's larger side, so the exact
/// target size, the look and the normal strength come from re-evaluating the project's rules against the stored source size; when the rules no longer
/// agree with the row, the row's target wins and the other side follows the source aspect ratio.
/// </summary>
/// <remarks>
/// The row stores the format name, not the DXGI number, so the sRGB flag of the source is not known here and outputs are tagged UNORM.
/// </remarks>
internal sealed partial class JobMapper
{
    private readonly RuleEvaluator _evaluator;
    private readonly ForgeRules _rules;
    private readonly string? _modelOverride;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="JobMapper"/> class.</summary>
    /// <param name="rules">The rules the project was scanned with.</param>
    /// <param name="modelOverride">A model that replaces every job's model (<c>--model</c>), or <see langword="null"/>.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    public JobMapper(ForgeRules rules, string? modelOverride = null, ILogger<JobMapper>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules;
        _evaluator = new RuleEvaluator(rules);
        _modelOverride = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the model used for <paramref name="job"/>.</summary>
    /// <param name="job">The job row.</param>
    /// <returns>The override, else the job's model.</returns>
    /// <exception cref="InvalidOperationException">The job has no model and there is no override.</exception>
    public string ModelFor(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _modelOverride ?? job.Model ?? throw new InvalidOperationException($"Job {job.Path} has no model; pass --model.");
    }

    /// <summary>Maps a job row.</summary>
    /// <param name="job">The job row.</param>
    /// <param name="textureOverride">The job's "This texture" look override (§8 scopes), or <see langword="null"/>.</param>
    /// <returns>The texture job.</returns>
    /// <exception cref="InvalidOperationException">The row has no usable source size or target.</exception>
    public TextureJob Map(JobRecord job, LookOverrides? textureOverride = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.SourceWidth <= 0 || job.SourceHeight <= 0 || job.Target <= 0)
        {
            throw new InvalidOperationException($"Job {job.Path} has no usable size (source {job.SourceWidth}×{job.SourceHeight}, target {job.Target}).");
        }

        var kind = Enum.TryParse<TextureKind>(job.Kind, ignoreCase: true, out var parsed) ? parsed : TextureClassifier.GetKind(job.Path);
        var decision = _evaluator.Evaluate(job.Path, new DdsHeader(job.SourceWidth, job.SourceHeight, 1, string.Empty, null, false));

        int width;
        int height;
        string? lookGroup = null;
        if (!decision.IsSkipped && Math.Max(decision.TargetWidth, decision.TargetHeight) == job.Target)
        {
            (width, height, lookGroup) = (decision.TargetWidth, decision.TargetHeight, decision.Group);
        }
        else
        {
            // The rules changed since the scan: the row's target wins. Keep the look (and normal strength) of a group that still
            // matches (by evaluation, else by the row's group name); otherwise fall back to neutral (relief unchanged).
            (width, height) = FitToMax(job.SourceWidth, job.SourceHeight, job.Target);
            if (!decision.IsSkipped)
            {
                lookGroup = decision.Group;
            }
            else if (LookResolver.FindGroup(_rules, job.Group) is { } group)
            {
                lookGroup = group.Name;
            }
            else
            {
                LogNoGroup(job.Path, job.Group);
            }
        }

        // Scopes (§8): texture override, else group, else the global look, else neutral. Without a group only the global look applies.
        LookSettings look = LookResolver.Resolve(_rules, lookGroup, textureOverride);
        return new TextureJob(job.Path, job.Archive, kind, width, height, ModelFor(job), look.NormalStrength, Look: look);
    }

    /// <summary>Scales a size so that its larger side equals <paramref name="target"/>, keeping the aspect ratio.</summary>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="target">Target larger side.</param>
    /// <returns>The target size; each side is at least 1.</returns>
    public static (int Width, int Height) FitToMax(int width, int height, int target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 1);

        return width >= height
            ? (target, Math.Max(1, (int)Math.Round((double)height * target / width)))
            : (Math.Max(1, (int)Math.Round((double)width * target / height)), target);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No rule group matches {Path} (scanned as {Group}) any more; using normal strength 1.0")]
    private partial void LogNoGroup(string path, string? group);
}
