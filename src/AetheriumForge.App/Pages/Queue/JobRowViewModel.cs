using System.Globalization;
using AetheriumForge.Core.Jobs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Queue;

/// <summary>
/// One row of the Queue grid. Rows are updated in place on refresh, so the grid keeps its scroll position and selection.
/// </summary>
public sealed partial class JobRowViewModel : ObservableObject
{
    /// <summary>Initialises a row from a job.</summary>
    /// <param name="job">The job.</param>
    public JobRowViewModel(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);

        Id = job.Id;
        Path = job.Path;
        Update(job);
    }

    /// <summary>Gets the job id.</summary>
    public long Id { get; }

    /// <summary>Gets the canonical game path.</summary>
    public string Path { get; }

    /// <summary>Gets the texture kind.</summary>
    [ObservableProperty]
    public partial string Kind { get; private set; } = string.Empty;

    /// <summary>Gets the rule group, or an empty string.</summary>
    [ObservableProperty]
    public partial string Group { get; private set; } = string.Empty;

    /// <summary>Gets the source size as <c>W×H</c>.</summary>
    [ObservableProperty]
    public partial string Source { get; private set; } = string.Empty;

    /// <summary>Gets the target maximum dimension in pixels.</summary>
    [ObservableProperty]
    public partial int Target { get; private set; }

    /// <summary>Gets the model, or an empty string.</summary>
    [ObservableProperty]
    public partial string Model { get; private set; } = string.Empty;

    /// <summary>Gets the lifecycle state.</summary>
    [ObservableProperty]
    public partial JobState State { get; private set; }

    /// <summary>Gets the failed attempts.</summary>
    [ObservableProperty]
    public partial int Attempts { get; private set; }

    /// <summary>Gets the processing time in seconds (one decimal), or an empty string.</summary>
    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    /// <summary>Gets the last error, or an empty string.</summary>
    [ObservableProperty]
    public partial string Error { get; private set; } = string.Empty;

    /// <summary>Copies <paramref name="job"/>'s values into the row; only changed values raise change notifications.</summary>
    /// <param name="job">The job's current record (same id).</param>
    /// <returns>This row.</returns>
    public JobRowViewModel Update(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);

        Kind = job.Kind;
        Group = job.Group ?? string.Empty;
        Source = $"{job.SourceWidth.ToString(CultureInfo.InvariantCulture)}×{job.SourceHeight.ToString(CultureInfo.InvariantCulture)}";
        Target = job.Target;
        Model = job.Model ?? string.Empty;
        State = job.State;
        Attempts = job.Attempts;
        Time = job.Seconds is { } seconds ? seconds.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
        Error = job.Error ?? string.Empty;

        return this;
    }
}
