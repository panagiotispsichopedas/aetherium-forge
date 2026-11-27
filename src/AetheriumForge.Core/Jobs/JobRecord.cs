namespace AetheriumForge.Core.Jobs;

/// <summary>
/// One row of the <c>job</c> table: a texture to upscale and its progress.
/// </summary>
/// <param name="Id">Database id; ignored when inserting.</param>
/// <param name="Path">Canonical game path, for example <c>textures\clutter\bucket01.dds</c>; unique.</param>
/// <param name="Archive">Source BSA the texture comes from.</param>
/// <param name="SourceWidth">Source width in pixels.</param>
/// <param name="SourceHeight">Source height in pixels.</param>
/// <param name="Format">Source DXGI/DDS format name.</param>
/// <param name="Kind">Texture kind (diffuse, normal, ...).</param>
/// <param name="Group">Matching rule group, or <see langword="null"/>.</param>
/// <param name="Target">Target maximum dimension in pixels.</param>
/// <param name="Model">Upscaling model, or <see langword="null"/> when none applies.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="Attempts">Number of failed attempts so far.</param>
/// <param name="Error">Last error message, or <see langword="null"/>.</param>
/// <param name="Seconds">Processing time of the successful run, or <see langword="null"/>.</param>
/// <param name="FinishedAt">When the job last finished (done or failed), or <see langword="null"/>.</param>
/// <param name="LookHash">
/// Stable hash of the effective look (§8, <see cref="Rules.LookSettings.GetStableHash"/>) the job was finished with, or
/// <see langword="null"/> when unknown (not finished, or finished before looks were recorded).
/// </param>
public sealed record JobRecord(
    long Id,
    string Path,
    string Archive,
    int SourceWidth,
    int SourceHeight,
    string Format,
    string Kind,
    string? Group,
    int Target,
    string? Model,
    JobState State,
    int Attempts,
    string? Error,
    double? Seconds,
    DateTimeOffset? FinishedAt,
    string? LookHash = null);
