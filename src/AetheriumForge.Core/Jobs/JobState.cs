namespace AetheriumForge.Core.Jobs;

/// <summary>
/// Lifecycle state of one texture job. Stored in the database by name (IMPLEMENTATION_GUIDE §12).
/// </summary>
public enum JobState
{
    /// <summary>Waiting to be processed.</summary>
    Queued,

    /// <summary>Taken by the pipeline; returns to <see cref="Queued"/> after a crash or stop.</summary>
    Running,

    /// <summary>Output written successfully.</summary>
    Done,

    /// <summary>Processing failed; see the job's error and attempts.</summary>
    Failed,

    /// <summary>Not processed, for example because a mod now provides the texture.</summary>
    Skipped,
}
