using System.Globalization;
using AetheriumForge.Core.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// One pipeline stage lane on the Run page: the current item, in-flight and queued counts, and tile progress for Upscale.
/// </summary>
public sealed partial class StageLaneViewModel : ObservableObject
{
    /// <summary>The text shown when the stage is idle.</summary>
    public const string IdleText = "idle";

    /// <summary>Initializes a new instance of the <see cref="StageLaneViewModel"/> class.</summary>
    /// <param name="stage">The stage.</param>
    public StageLaneViewModel(PipelineStage stage)
    {
        Stage = stage;
        Title = stage switch
        {
            PipelineStage.PostProcess => "Post-process",
            _ => stage.ToString(),
        };
    }

    /// <summary>Gets the stage.</summary>
    public PipelineStage Stage { get; }

    /// <summary>Gets the lane title.</summary>
    public string Title { get; }

    /// <summary>Gets a value indicating whether the lane shows tile progress (Upscale only).</summary>
    public bool ShowsTiles => Stage == PipelineStage.Upscale;

    /// <summary>Gets the file name of the current item, or <see cref="IdleText"/>.</summary>
    [ObservableProperty]
    private string _currentItem = IdleText;

    /// <summary>Gets the full path of the current item (tooltip), or <see langword="null"/>.</summary>
    [ObservableProperty]
    private string? _currentPath;

    /// <summary>Gets the in-flight and queued counts.</summary>
    [ObservableProperty]
    private string _countsText = "0 in flight · 0 queued";

    /// <summary>Gets the tile progress in percent (Upscale).</summary>
    [ObservableProperty]
    private double _tilePercent;

    /// <summary>Gets the tile progress text (Upscale), for example <c>12 / 48 tiles</c>.</summary>
    [ObservableProperty]
    private string _tilesText = string.Empty;

    /// <summary>Applies a progress entry.</summary>
    /// <param name="activity">The stage's activity.</param>
    public void Update(StageActivity activity)
    {
        CurrentPath = activity.CurrentPath;
        CurrentItem = activity.CurrentPath is { Length: > 0 } path ? Path.GetFileName(path) : IdleText;
        CountsText = string.Create(CultureInfo.InvariantCulture, $"{activity.InFlight} in flight · {activity.Queued} queued");
        if (ShowsTiles)
        {
            TilePercent = activity.TilesTotal > 0 ? 100.0 * activity.TilesDone / activity.TilesTotal : 0;
            TilesText = activity.TilesTotal > 0 ? string.Create(CultureInfo.InvariantCulture, $"{activity.TilesDone} / {activity.TilesTotal} tiles") : string.Empty;
        }
    }
}
