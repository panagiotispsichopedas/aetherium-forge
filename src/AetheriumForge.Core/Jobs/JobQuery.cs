namespace AetheriumForge.Core.Jobs;

/// <summary>
/// Column a <see cref="JobQuery"/> sorts by.
/// </summary>
public enum JobSortColumn
{
    /// <summary>Database id (scan order).</summary>
    Id,

    /// <summary>Canonical game path.</summary>
    Path,

    /// <summary>Texture kind.</summary>
    Kind,

    /// <summary>Rule group.</summary>
    Group,

    /// <summary>Source size (width × height, by pixel count).</summary>
    Source,

    /// <summary>Target maximum dimension.</summary>
    Target,

    /// <summary>Upscaling model.</summary>
    Model,

    /// <summary>Lifecycle state, in <see cref="JobState"/> order.</summary>
    State,

    /// <summary>Failed attempts.</summary>
    Attempts,

    /// <summary>Processing time in seconds.</summary>
    Seconds,

    /// <summary>Last error message.</summary>
    Error,
}

/// <summary>
/// A filtered, sorted, paged view of the <c>job</c> table. Every value becomes a SQL parameter; nothing is concatenated into SQL.
/// </summary>
public sealed record JobQuery
{
    /// <summary>Gets the query that returns every job in id order.</summary>
    public static JobQuery All { get; } = new();

    /// <summary>Gets the states to include; <see langword="null"/> includes every state and an empty set matches nothing.</summary>
    public IReadOnlySet<JobState>? States { get; init; }

    /// <summary>Gets the exact rule group to match, or <see langword="null"/> for any group.</summary>
    public string? Group { get; init; }

    /// <summary>Gets the exact texture kind to match, or <see langword="null"/> for any kind.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Gets text the path must contain (case-insensitive; <c>/</c> matches <c>\</c>; <c>%</c> and <c>_</c> are literal), or
    /// <see langword="null"/>/blank for no text filter.
    /// </summary>
    public string? PathContains { get; init; }

    /// <summary>Gets the sort column. Ties are broken by id in the same direction.</summary>
    public JobSortColumn SortBy { get; init; } = JobSortColumn.Id;

    /// <summary>Gets a value indicating whether the sort is descending.</summary>
    public bool Descending { get; init; }

    /// <summary>Gets the number of rows to skip.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the maximum number of rows to return, or <see langword="null"/> for no limit.</summary>
    public int? Limit { get; init; }
}
