using System.Globalization;
using AetheriumForge.Core.Game;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Projects;
using AetheriumForge.Core.Rules;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Services;

/// <summary>
/// The app's open project (IMPLEMENTATION_GUIDE §12, §14): opens or creates a project folder, owns its <see cref="JobStore"/>, remembers
/// recent projects and raises <see cref="Changed"/> on the UI thread. Never writes into the game folder: every write goes through
/// <see cref="ProjectLocator.EnsureOutsideGame"/> first.
/// </summary>
/// <remarks>
/// One gate serialises opening, importing, reading and disposing, so a read never sees a store that a project switch has just disposed.
/// <see cref="Changed"/> is raised after the gate is released, once per committed switch.
/// </remarks>
public sealed partial class ProjectSession : IScanProjectSession, IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for a running open or import to finish.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(10);

    private readonly IAppSettingsStore _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectSession> _logger;
    private readonly Action<Action> _postToUi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _recent = [];
    private bool _recentLoaded;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ProjectSession"/> class.</summary>
    /// <param name="settings">The app settings (recent projects).</param>
    /// <param name="timeProvider">Clock for the scan time.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <param name="postToUi">Runs an action on the UI thread; <see langword="null"/> uses <see cref="Dispatcher.UIThread"/>. Tests pass an inline runner.</param>
    public ProjectSession(IAppSettingsStore settings, TimeProvider timeProvider, ILogger<ProjectSession>? logger = null, Action<Action>? postToUi = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<ProjectSession>.Instance;
        _postToUi = postToUi ?? PostToDispatcher;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public event EventHandler? JobsChanged;

    /// <inheritdoc />
    public ProjectInfo? Current { get; private set; }

    /// <inheritdoc />
    public JobStore? Store { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> RecentProjects => _recent;

    /// <inheritdoc />
    public async Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);

        var committed = false;
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var layout = ProjectLayout.For(projectFolder);
            if (!File.Exists(layout.DatabasePath))
            {
                throw new ProjectException($"{layout.Root} is not an Aetherium Forge project (no {ProjectLayout.DatabaseFileName}).");
            }

            // Read the game path without opening a JobStore (which writes the schema): the containment check comes before any write.
            var gamePath = await ProjectDatabaseReader.ReadProjectValueAsync(layout.DatabasePath, ProjectKeys.GamePath, cancellationToken).ConfigureAwait(false)
                ?? throw new ProjectException($"The project {layout.Root} has no game folder recorded. Scan the game into it again.");
            ProjectLocator.EnsureOutsideGame(layout, gamePath);

            var store = await JobStore.OpenAsync(layout.DatabasePath, _timeProvider, cancellationToken).ConfigureAwait(false);
            ForgeRules rules;
            try
            {
                var rulesJson = await store.GetProjectValueAsync(ProjectKeys.RulesJson, cancellationToken).ConfigureAwait(false);
                rules = rulesJson is null ? ForgeRules.Default : ForgeRules.FromJson(rulesJson);
            }
            catch
            {
                await store.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await ReplaceAsync(layout, gamePath, rules, store).ConfigureAwait(false);
            committed = true;
            LogOpened(_logger, layout.Root, gamePath);
            await RememberAsync(layout.Root).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            if (committed)
            {
                RaiseChanged();
            }
        }
    }

    /// <inheritdoc />
    public async Task<ScanImportResult> ApplyScanAsync(ScanImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var committed = false;
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var layout = ProjectLayout.For(request.ProjectFolder);
            var gameFolder = request.Game.GameFolder;
            ProjectLocator.EnsureOutsideGame(layout, gameFolder);
            Directory.CreateDirectory(layout.Root);

            var reuse = Store is not null && Current is not null && string.Equals(Current.Root, layout.Root, StringComparison.OrdinalIgnoreCase);
            var store = reuse ? Store! : await JobStore.OpenAsync(layout.DatabasePath, _timeProvider, cancellationToken).ConfigureAwait(false);
            ScanImportResult imported;
            try
            {
                // Jobs and project values go in one transaction: a failed import leaves the project exactly as it was.
                var values = new Dictionary<string, string>
                {
                    [ProjectKeys.GamePath] = gameFolder,
                    [ProjectKeys.GameStore] = request.Game.Store.ToString(),
                    [ProjectKeys.RulesJson] = request.Rules.ToJson(),
                    [ProjectKeys.ScannedAt] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                };
                var (upserted, nowProvided) = await ScanImporter.ImportAsync(store, request.Result, values, cancellationToken).ConfigureAwait(false);
                var counts = await store.CountByStateAsync(CancellationToken.None).ConfigureAwait(false);
                imported = new ScanImportResult(layout.DatabasePath, upserted, nowProvided, counts, request.Result.MissingArchives);
            }
            catch
            {
                if (!reuse)
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            await ReplaceAsync(layout, gameFolder, request.Rules, store).ConfigureAwait(false);
            committed = true;
            LogScanImported(_logger, imported.Upserted, layout.Root);
            await RememberAsync(layout.Root).ConfigureAwait(false);
            return imported;
        }
        finally
        {
            _gate.Release();
            if (committed)
            {
                RaiseChanged();
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetProjectValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Store is { } store ? await store.GetProjectValueAsync(key, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> GetJobsAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Store is not { } store)
            {
                return [];
            }

            var jobs = new List<JobRecord>();
            await foreach (var job in store.GetJobsAsync(null, cancellationToken).ConfigureAwait(false))
            {
                jobs.Add(job);
            }

            return jobs;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReopenLastAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadRecentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var folder in _recent)
        {
            try
            {
                if (!File.Exists(ProjectLayout.For(folder).DatabasePath))
                {
                    continue;
                }

                await OpenAsync(folder, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (ProjectException ex)
            {
                LogReopenFailed(_logger, ex, folder);
            }
            catch (ArgumentException ex)
            {
                LogReopenFailed(_logger, ex, folder);
                continue;
            }
            catch (IOException ex)
            {
                LogReopenFailed(_logger, ex, folder);
            }
            catch (SqliteException ex)
            {
                LogReopenFailed(_logger, ex, folder);
            }

            // Only the most recent existing project is tried: an older one would surprise the user.
            return false;
        }

        return false;
    }

    /// <inheritdoc />
    public void NotifyJobsChanged() => JobsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Releases the job store's pooled connections, after any running open or import finished.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Wait for a running open or import (the app-start reopen is cancelled before this runs), so the store is not disposed under it.
        var entered = _gate.Wait(DisposeWait);
        try
        {
            _disposed = true;

            // JobStore.DisposeAsync only clears the connection pool and always completes synchronously, so nothing blocks here.
            var pending = Store?.DisposeAsync() ?? ValueTask.CompletedTask;
            if (!pending.IsCompletedSuccessfully)
            {
                pending.AsTask().GetAwaiter().GetResult();
            }

            Store = null;
            Current = null;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }

        if (!entered)
        {
            LogDisposeTimedOut(_logger, DisposeWait.TotalSeconds);
        }
    }

    private static void PostToDispatcher(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(ProjectSession));
        }
    }

    private async Task ReplaceAsync(ProjectLayout layout, string gameFolder, ForgeRules rules, JobStore store)
    {
        if (Store is { } previous && !ReferenceEquals(previous, store))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        Store = store;
        Current = new ProjectInfo(layout.Root, gameFolder, Path.Combine(gameFolder, GameProfileLoader.DataFolderName), layout.OutputRoot, layout.WorkRoot, rules);
    }

    private async Task LoadRecentAsync(CancellationToken cancellationToken)
    {
        if (!_recentLoaded)
        {
            _recent = (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).RecentProjects;
            _recentLoaded = true;
        }
    }

    /// <summary>Puts the project first in the recent list. Called under the gate after a committed switch, so it is never cancelled.</summary>
    private async Task RememberAsync(string projectFolder)
    {
        try
        {
            await LoadRecentAsync(CancellationToken.None).ConfigureAwait(false);
            _recent = AppSettingsStore.PushRecent(_recent, projectFolder);
            await _settings.SaveAsync(new AppSettings { RecentProjects = _recent }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // The project is open; only the recent list could not be saved.
            LogSettingsSaveFailed(_logger, ex, _settings.FilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogSettingsSaveFailed(_logger, ex, _settings.FilePath);
        }
    }

    private void RaiseChanged() => _postToUi(() => Changed?.Invoke(this, EventArgs.Empty));

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Opened project {Project} for {Game}")]
    private static partial void LogOpened(ILogger logger, string project, string game);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Imported {Rows} scan rows into {Project}")]
    private static partial void LogScanImported(ILogger logger, int rows, string project);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Could not reopen the recent project {Project}")]
    private static partial void LogReopenFailed(ILogger logger, Exception exception, string project);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Could not save the app settings file {Path}")]
    private static partial void LogSettingsSaveFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "A project open or import was still running after {Seconds} s at shutdown; the store was released anyway")]
    private static partial void LogDisposeTimedOut(ILogger logger, double seconds);
}
