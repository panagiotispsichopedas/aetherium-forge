using System.Globalization;
using System.Text.Json;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Re-evaluates existing job rows against an edited <see cref="ForgeRules"/> set (IMPLEMENTATION_GUIDE §8, §14 "Rules"): live counts per
/// group, estimated output size and time, and the row changes that applying the rules would make. Pure and thread-safe.
/// </summary>
/// <remarks>
/// <para>Only rows the rules decide are re-evaluated: <see cref="JobState.Queued"/> rows and <see cref="JobState.Skipped"/> rows whose
/// error is a rule <see cref="SkipReason"/>. <see cref="JobState.Running"/> and <see cref="JobState.Failed"/> rows are never changed, and rows
/// skipped for another reason (cube map, invalid header, "now provided by a mod") keep their state.</para>
/// <para><see cref="JobState.Done"/> rows whose result would change (a new target or model, or the rules now skip them) are listed separately in
/// <see cref="RulesPreview.DoneChanges"/> and are never part of <see cref="RulesPreview.Changes"/>, so finished work is not redone silently.</para>
/// </remarks>
public static class RulesApplier
{
    /// <summary>The project key/value entry that holds the rules JSON (the same key the CLI scan writes).</summary>
    public const string RulesProjectKey = "rulesJson";

    /// <summary>Estimated BC7 output bytes per pixel including the mip chain: 1 byte per pixel × 4/3.</summary>
    public const double Bc7BytesPerPixelWithMips = 4.0 / 3.0;

    /// <summary>Skip reasons that only the scan can decide; rows skipped for these keep their state.</summary>
    private static readonly SkipReason[] _scanOnlyReasons = [SkipReason.Cubemap, SkipReason.InvalidHeader];

    /// <summary>Names of the skip reasons the rules decide (the error text the scan writes for them).</summary>
    private static readonly HashSet<string> _ruleSkipNames =
        [.. Enum.GetValues<SkipReason>().Where(r => r != SkipReason.None && !_scanOnlyReasons.Contains(r)).Select(r => r.ToString())];

    /// <summary>
    /// Evaluates every row against <paramref name="rules"/> and returns the counts, estimates and changes.
    /// </summary>
    /// <param name="jobs">The project's job rows.</param>
    /// <param name="rules">The edited rules. They must be valid (see <see cref="Validate"/>).</param>
    /// <param name="options">Optional timings and the look comparison for finished rows.</param>
    /// <param name="ct">Cancels the evaluation (checked between rows).</param>
    /// <returns>The preview.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jobs"/> or <paramref name="rules"/> is <see langword="null"/>.</exception>
    public static RulesPreview Preview(IEnumerable<JobRecord> jobs, ForgeRules rules, RulesPreviewOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(rules);

        Func<string, double?>? secondsPerSourceMegapixel = options?.SecondsPerSourceMegapixel;
        Func<JobRecord, GroupRule, bool>? doneLookDiffers = options?.DoneLookDiffers;

        // Evaluate with groups renamed to their index, so counts stay per group even when two groups share a name.
        var indexed = rules with { Groups = [.. rules.Groups.Select((g, i) => g with { Name = i.ToString(CultureInfo.InvariantCulture) })] };
        var evaluator = new RuleEvaluator(indexed);

        int groupCount = rules.Groups.Count;
        var counts = new int[groupCount];
        var bytes = new long[groupCount];
        var seconds = new double[groupCount];
        var untimed = new int[groupCount];
        var skippedByReason = new Dictionary<SkipReason, int>();
        var changes = new List<JobRuleChange>();
        var doneChanges = new List<JobRuleChange>();
        int notRuleManaged = 0;
        int total = 0;

        foreach (JobRecord job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            total++;

            if (job.State == JobState.Skipped && !IsRuleSkip(job.Error))
            {
                notRuleManaged++;
                continue;
            }

            var header = new DdsHeader(job.SourceWidth, job.SourceHeight, 1, string.Empty, null, IsCubemap: false);
            TextureDecision decision = evaluator.Evaluate(job.Path, header);
            int? groupIndex = decision.Group is { } g ? int.Parse(g, CultureInfo.InvariantCulture) : null;
            string? groupName = groupIndex is int gi ? rules.Groups[gi].Name : null;

            if (decision.IsSkipped)
            {
                skippedByReason[decision.Skip] = skippedByReason.GetValueOrDefault(decision.Skip) + 1;
            }
            else
            {
                int index = groupIndex!.Value;
                counts[index]++;
                bytes[index] += (long)Math.Ceiling((double)decision.TargetWidth * decision.TargetHeight * Bc7BytesPerPixelWithMips);
                // Time covers the rows a run would process: Queued, Running, and rule-skipped rows these rules queue. Failed and Done rows wait
                // for an explicit retry or re-queue.
                if (job.State is JobState.Queued or JobState.Running or JobState.Skipped)
                {
                    if (secondsPerSourceMegapixel?.Invoke(decision.Model!) is double spm)
                    {
                        seconds[index] += spm * job.SourceWidth * job.SourceHeight / 1_000_000.0;
                    }
                    else
                    {
                        untimed[index]++;
                    }
                }
            }

            GroupRule? groupRule = groupIndex is int ri ? rules.Groups[ri] : null;
            if (ToChange(job, decision, groupName, groupRule, doneLookDiffers) is { } change)
            {
                (job.State == JobState.Done ? doneChanges : changes).Add(change);
            }
        }

        bool timed = secondsPerSourceMegapixel is not null;
        var groups = new GroupEstimate[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            groups[i] = new GroupEstimate(i, rules.Groups[i].Name, counts[i], bytes[i], timed ? seconds[i] : null, timed ? untimed[i] : counts[i]);
        }

        return new RulesPreview(total, groups, skippedByReason, notRuleManaged, changes, doneChanges);
    }

    /// <summary>
    /// Returns a <see cref="RulesPreviewOptions.DoneLookDiffers"/> comparison against the rules the finished rows were made with:
    /// a finished row's look differs when the new group's normal strength or look differs from its previous group's (matched by name,
    /// case-insensitively). A row whose previous group is unknown counts as different.
    /// </summary>
    /// <param name="previous">The rules stored in the project before the edit.</param>
    /// <returns>The comparison.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> is <see langword="null"/>.</exception>
    public static Func<JobRecord, GroupRule, bool> LookDiffersFrom(ForgeRules previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var byName = new Dictionary<string, GroupRule>(StringComparer.OrdinalIgnoreCase);
        foreach (GroupRule group in previous.Groups)
        {
            byName.TryAdd(group.Name, group);
        }

        return (job, group) =>
            job.Group is null
            || !byName.TryGetValue(job.Group, out GroupRule? old)
            || !old.NormalStrength.Equals(group.NormalStrength)
            || !Equals(old.Look, group.Look);
    }

    /// <summary>
    /// Validates <paramref name="rules"/> with the same checks as <see cref="ForgeRules.FromJson"/>, plus the editor's checks: at least one
    /// group, and unique group names (case-insensitive).
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <returns>The first problem found, or <see langword="null"/> when the rules are valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    public static string? Validate(ForgeRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Groups is { Count: 0 })
        {
            return "At least one group is required; with none, every texture is skipped.";
        }

        if (rules.Groups?.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name))
                .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return $"Group names must be unique: '{duplicate.Key}' is used more than once.";
        }

        try
        {
            rules.Validate();
            return null;
        }
        catch (JsonException ex)
        {
            // Validation reports problems as JsonException (it backs FromJson); here the message is the result.
            return ex.Message;
        }
    }

    /// <summary>Returns whether a Skipped row's error is a skip reason the rules decide (so new rules may re-queue it).</summary>
    /// <param name="error">The row's error column.</param>
    /// <returns><see langword="true"/> for a rule skip reason.</returns>
    public static bool IsRuleSkip(string? error) => error is not null && _ruleSkipNames.Contains(error);

    private static JobRuleChange? ToChange(JobRecord job, TextureDecision decision, string? groupName, GroupRule? groupRule, Func<JobRecord, GroupRule, bool>? doneLookDiffers)
    {
        JobState newState = decision.IsSkipped ? JobState.Skipped : JobState.Queued;
        int target = Math.Max(decision.TargetWidth, decision.TargetHeight);
        string? error = decision.IsSkipped ? decision.Skip.ToString() : null;

        switch (job.State)
        {
            case JobState.Queued:
            case JobState.Skipped:
                bool same = job.State == newState
                    && string.Equals(job.Group, groupName, StringComparison.Ordinal)
                    && job.Target == target
                    && string.Equals(job.Model, decision.Model, StringComparison.Ordinal)
                    && string.Equals(job.Error, error, StringComparison.Ordinal);
                return same ? null : new JobRuleChange(job.Id, job.Path, job.State, newState, groupName, target, decision.Model, error);

            case JobState.Done:
                // A finished texture changes only when its output would differ: the rules now skip it, or its target, model, normal
                // strength or look changed.
                bool unchanged = !decision.IsSkipped
                    && job.Target == target
                    && string.Equals(job.Model, decision.Model, StringComparison.Ordinal)
                    && !(groupRule is not null && doneLookDiffers?.Invoke(job, groupRule) == true);
                return unchanged ? null : new JobRuleChange(job.Id, job.Path, job.State, newState, groupName, target, decision.Model, error);

            default:
                return null;
        }
    }
}

/// <summary>
/// Options for <see cref="RulesApplier.Preview"/>.
/// </summary>
public sealed record RulesPreviewOptions
{
    /// <summary>
    /// Gets a lookup from a model name to measured seconds per source megapixel (from the GPU profile); it returns <see langword="null"/> for
    /// an uncalibrated model. When <see langword="null"/>, no time is estimated.
    /// </summary>
    public Func<string, double?>? SecondsPerSourceMegapixel { get; init; }

    /// <summary>
    /// Gets a comparison that returns <see langword="true"/> when a finished row was made with a different look or normal strength than
    /// its new group (see <see cref="RulesApplier.LookDiffersFrom"/>). When <see langword="null"/>, only target and model are compared.
    /// </summary>
    public Func<JobRecord, GroupRule, bool>? DoneLookDiffers { get; init; }
}

/// <summary>
/// The result of <see cref="RulesApplier.Preview"/>.
/// </summary>
/// <param name="TotalJobs">Every row evaluated or passed over.</param>
/// <param name="Groups">One estimate per rule group, in rule order.</param>
/// <param name="SkippedByReason">Rows the rules skip, per reason.</param>
/// <param name="NotRuleManaged">Rows skipped for a reason the rules do not decide (cube map, invalid header, provided by a mod).</param>
/// <param name="Changes">Changes for Queued and rule-Skipped rows; safe to apply.</param>
/// <param name="DoneChanges">Finished rows whose output would change; apply only after the user agrees.</param>
public sealed record RulesPreview(
    int TotalJobs,
    IReadOnlyList<GroupEstimate> Groups,
    IReadOnlyDictionary<SkipReason, int> SkippedByReason,
    int NotRuleManaged,
    IReadOnlyList<JobRuleChange> Changes,
    IReadOnlyList<JobRuleChange> DoneChanges)
{
    /// <summary>Gets the number of rows the rules skip (all reasons).</summary>
    public int SkippedCount => SkippedByReason.Values.Sum();

    /// <summary>Gets the number of rows the rules upscale.</summary>
    public int UpscaledCount => Groups.Sum(g => g.Count);

    /// <summary>Gets the estimated total output size in bytes (BC7 with mips).</summary>
    public long EstimatedBytes => Groups.Sum(g => g.EstimatedBytes);

    /// <summary>Gets the estimated processing time of the rows that are not finished, or <see langword="null"/> without timings.</summary>
    public double? EstimatedSeconds => Groups.All(g => g.EstimatedSeconds is null) ? null : Groups.Sum(g => g.EstimatedSeconds ?? 0);

    /// <summary>Gets the number of unfinished upscaled rows whose model has no timing (not part of <see cref="EstimatedSeconds"/>).</summary>
    public int UntimedCount => Groups.Sum(g => g.UntimedCount);
}

/// <summary>
/// Live count and estimates for one rule group.
/// </summary>
/// <param name="Index">The group's position in the rules.</param>
/// <param name="Name">The group name.</param>
/// <param name="Count">Rows the group upscales (finished rows included).</param>
/// <param name="EstimatedBytes">Estimated output size in bytes: target width × height × 4/3 (BC7 with mips), summed.</param>
/// <param name="EstimatedSeconds">Estimated processing time of the unfinished rows, or <see langword="null"/> without timings.</param>
/// <param name="UntimedCount">Unfinished rows whose model has no timing.</param>
public sealed record GroupEstimate(int Index, string Name, int Count, long EstimatedBytes, double? EstimatedSeconds, int UntimedCount);

/// <summary>
/// One row update produced by <see cref="RulesApplier.Preview"/> and applied by <see cref="JobStore.ApplyRuleChangesAsync(IEnumerable{JobRuleChange}, string?, CancellationToken)"/>.
/// </summary>
/// <param name="Id">The job id.</param>
/// <param name="Path">The canonical path (for display).</param>
/// <param name="OldState">The state the row had when evaluated; the update applies only while the row is still in it.</param>
/// <param name="NewState"><see cref="JobState.Queued"/> or <see cref="JobState.Skipped"/>.</param>
/// <param name="Group">The new group name, or <see langword="null"/>.</param>
/// <param name="Target">The new target max dimension; 0 when skipped.</param>
/// <param name="Model">The new model, or <see langword="null"/> when skipped.</param>
/// <param name="Error">The skip reason name when skipped, otherwise <see langword="null"/>.</param>
public sealed record JobRuleChange(long Id, string Path, JobState OldState, JobState NewState, string? Group, int Target, string? Model, string? Error);
