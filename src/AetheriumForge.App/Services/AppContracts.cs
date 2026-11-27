using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.App.Services;

/// <summary>
/// A project the app is working on: one game install, one job store, one output folder.
/// </summary>
/// <param name="Root">The project folder (holds <c>forge.db</c>, <c>work\</c>, <c>output\</c>, <c>logs\</c>).</param>
/// <param name="GameFolder">The game folder (read only, never written).</param>
/// <param name="DataFolder">The game's <c>Data</c> folder.</param>
/// <param name="OutputRoot">Mod output folder; textures go to <c>&lt;OutputRoot&gt;\textures</c>.</param>
/// <param name="WorkRoot">Temporary folder for the pipeline.</param>
/// <param name="Rules">The rules the project was scanned with.</param>
public sealed record ProjectInfo(string Root, string GameFolder, string DataFolder, string OutputRoot, string WorkRoot, ForgeRules Rules);

/// <summary>
/// The open project, shared by every screen. Implemented by the Scan/shell work; Queue and Run only consume it.
/// </summary>
public interface IProjectSession
{
    /// <summary>Raised on the UI thread after a project is opened, created, rescanned or closed.</summary>
    event EventHandler? Changed;

    /// <summary>Raised (any thread) when job rows changed in bulk (import, rescan, Queue actions, pipeline runs).</summary>
    event EventHandler? JobsChanged;

    /// <summary>Gets the open project, or <see langword="null"/>.</summary>
    ProjectInfo? Current { get; }

    /// <summary>Gets the open project's job store, or <see langword="null"/>. Owned by the session; never dispose it.</summary>
    JobStore? Store { get; }

    /// <summary>Opens an existing project folder (one that holds <c>forge.db</c>).</summary>
    /// <param name="projectFolder">The project folder.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A task that completes when the project is open.</returns>
    Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default);

    /// <summary>Raises <see cref="JobsChanged"/>; call after changing job rows.</summary>
    void NotifyJobsChanged();
}

/// <summary>
/// Runs the pipeline for the open project and broadcasts its progress. A singleton, so a run survives page navigation.
/// </summary>
public interface IForgeRunner
{
    /// <summary>Raised (throttled, any thread) with each progress snapshot.</summary>
    event EventHandler<PipelineProgress>? ProgressChanged;

    /// <summary>Raised (any thread) when a run starts or ends, with the summary when it ended.</summary>
    event EventHandler<PipelineRunSummary?>? RunStateChanged;

    /// <summary>Gets the pipeline state.</summary>
    PipelineState State { get; }

    /// <summary>Gets the latest progress snapshot, or <see langword="null"/> before the first run.</summary>
    PipelineProgress? LastProgress { get; }

    /// <summary>Starts a run for the open project. Throws when one is already running.</summary>
    /// <param name="request">Run options.</param>
    /// <returns>The run; completes when it ends (also on stop).</returns>
    Task<PipelineRunSummary> StartAsync(ForgeRunRequest request);

    /// <summary>Pauses the running pipeline.</summary>
    void Pause();

    /// <summary>Resumes a paused pipeline (the Resume button). Named to avoid the reserved word <c>Resume</c> (CA1716).</summary>
    void Unpause();

    /// <summary>Requests a graceful stop (the Stop button). Named to avoid the reserved word <c>Stop</c> (CA1716).</summary>
    void RequestStop();
}

/// <summary>
/// Options for one run.
/// </summary>
/// <param name="ModelOverride">A model for every job, or <see langword="null"/>.</param>
/// <param name="MaxJobs">Process at most this many jobs, or <see langword="null"/>.</param>
public sealed record ForgeRunRequest(string? ModelOverride = null, int? MaxJobs = null);

/// <summary>
/// Pages in the main window's navigation, in display order.
/// </summary>
public enum AppPage
{
    /// <summary>Game detection, project and scan.</summary>
    Scan,

    /// <summary>The job list.</summary>
    Queue,

    /// <summary>Start, pause, resume, stop and live progress.</summary>
    Run,

    /// <summary>Installed and downloadable models, GPU profile.</summary>
    Models,
}

/// <summary>
/// Switches the main window's page.
/// </summary>
public interface INavigationService
{
    /// <summary>Raised on the UI thread after the page changed.</summary>
    event EventHandler<AppPage>? Navigated;

    /// <summary>Gets the current page.</summary>
    AppPage Current { get; }

    /// <summary>Shows <paramref name="page"/>.</summary>
    /// <param name="page">The page.</param>
    void NavigateTo(AppPage page);
}
