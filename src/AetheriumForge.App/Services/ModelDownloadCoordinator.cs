using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AetheriumForge.Core.Models;
using AetheriumForge.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.App.Services;

/// <summary>
/// The state of one model download.
/// </summary>
public enum ModelDownloadStatus
{
    /// <summary>Transferring.</summary>
    Downloading,

    /// <summary>Paused; the connection is abandoned at the next chunk and resumed with an HTTP range.</summary>
    Paused,

    /// <summary>Verified and installed.</summary>
    Completed,

    /// <summary>Cancelled by the user; the partial file is kept so a later download resumes it.</summary>
    Cancelled,

    /// <summary>Failed; see <see cref="ModelDownloadOperation.Error"/>.</summary>
    Failed,
}

/// <summary>
/// One model download run by <see cref="ModelDownloadCoordinator"/>. Its state changes on the download's thread; <see cref="Changed"/>
/// is raised there too, so UI code must marshal to the UI thread.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The token source is disposed when the download finishes; a finished operation needs nothing released.")]
public sealed class ModelDownloadOperation : IProgress<DownloadProgress>
{
    private readonly PauseTokenSource _pause = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _gate = new();

    internal ModelDownloadOperation(ManifestModel model)
    {
        Model = model;
    }

    /// <summary>Raised (any thread) when <see cref="Status"/> or <see cref="Progress"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the manifest entry being downloaded.</summary>
    public ManifestModel Model { get; }

    /// <summary>Gets the model name.</summary>
    public string Name => Model.Name;

    /// <summary>Gets the status.</summary>
    public ModelDownloadStatus Status { get; private set; } = ModelDownloadStatus.Downloading;

    /// <summary>Gets the latest progress, or <see langword="null"/> before the first report.</summary>
    public DownloadProgress? Progress { get; private set; }

    /// <summary>Gets the error of a failed download.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Gets the installed model after completion.</summary>
    public InstalledModel? Result { get; private set; }

    /// <summary>Gets a value indicating whether the download is still running (downloading or paused).</summary>
    public bool IsActive => Status is ModelDownloadStatus.Downloading or ModelDownloadStatus.Paused;

    /// <summary>Gets a task that completes when the download ends, whatever the outcome. It never faults.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    internal PauseToken PauseToken => _pause.Token;

    internal CancellationToken CancellationToken => _cancel.Token;

    /// <summary>Pauses the transfer.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (Status != ModelDownloadStatus.Downloading)
            {
                return;
            }

            _pause.Pause();
            Status = ModelDownloadStatus.Paused;
        }

        OnChanged();
    }

    /// <summary>Resumes a paused transfer. Named to avoid the reserved word <c>Resume</c> (CA1716).</summary>
    public void Unpause()
    {
        lock (_gate)
        {
            if (Status != ModelDownloadStatus.Paused)
            {
                return;
            }

            _pause.Resume();
            Status = ModelDownloadStatus.Downloading;
        }

        OnChanged();
    }

    /// <summary>Cancels the transfer; the partial file is kept for a later resume.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!IsActive)
            {
                return;
            }

            // A paused transfer waits on the pause token with the same cancellation token, so it ends too.
            _cancel.Cancel();
        }
    }

    /// <inheritdoc />
    void IProgress<DownloadProgress>.Report(DownloadProgress value)
    {
        Progress = value;
        OnChanged();
    }

    internal void Finish(ModelDownloadStatus status, InstalledModel? result = null, Exception? error = null)
    {
        lock (_gate)
        {
            Status = status;
            Result = result;
            Error = error;
            _cancel.Dispose();
        }

        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Runs model downloads for the whole app (a singleton), so a download keeps going while the user is on another page
/// (IMPLEMENTATION_GUIDE §9, "Model download").
/// </summary>
public sealed partial class ModelDownloadCoordinator : IDisposable
{
    private readonly Func<ModelLibrary> _libraryFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ModelDownloadOperation> _operations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDownloadCoordinator"/> class.
    /// </summary>
    /// <param name="libraryFactory">Creates the model library used for a download.</param>
    /// <param name="logger">Receives download events.</param>
    public ModelDownloadCoordinator(Func<ModelLibrary> libraryFactory, ILogger<ModelDownloadCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(libraryFactory);
        _libraryFactory = libraryFactory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised (any thread) when any download's status or progress changes.</summary>
    public event EventHandler<ModelDownloadOperation>? OperationChanged;

    /// <summary>Gets a value indicating whether any download is downloading or paused.</summary>
    public bool HasActiveDownloads => _operations.Values.Any(operation => operation.IsActive);

    /// <summary>Gets the latest download of every model started in this session.</summary>
    public IReadOnlyList<ModelDownloadOperation> Operations => [.. _operations.Values];

    /// <summary>
    /// Finds the latest download of a model.
    /// </summary>
    /// <param name="name">The model name.</param>
    /// <returns>The download, or <see langword="null"/> when none was started in this session.</returns>
    public ModelDownloadOperation? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _operations.TryGetValue(name, out var operation) ? operation : null;
    }

    /// <summary>
    /// Starts downloading a model, resuming a partial file when there is one. The caller has already asked the user to confirm.
    /// </summary>
    /// <param name="model">The manifest entry.</param>
    /// <returns>The new download, or the running one when the model is already downloading.</returns>
    public ModelDownloadOperation Start(ManifestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        while (true)
        {
            if (_operations.TryGetValue(model.Name, out var existing))
            {
                if (existing.IsActive)
                {
                    return existing;
                }

                var replacement = new ModelDownloadOperation(model);
                if (!_operations.TryUpdate(model.Name, replacement, existing))
                {
                    continue;
                }

                Run(replacement);
                return replacement;
            }

            var operation = new ModelDownloadOperation(model);
            if (_operations.TryAdd(model.Name, operation))
            {
                Run(operation);
                return operation;
            }
        }
    }

    /// <summary>Cancels every running download (on shutdown); partial files are kept.</summary>
    public void Dispose()
    {
        foreach (var operation in _operations.Values)
        {
            operation.Cancel();
        }
    }

    private void Run(ModelDownloadOperation operation)
    {
        operation.Changed += (_, _) => OperationChanged?.Invoke(this, operation);
        LogStarted(_logger, operation.Name, operation.Model.Size);
        operation.Completion = RunAsync(operation);
    }

    private async Task RunAsync(ModelDownloadOperation operation)
    {
        try
        {
            var library = _libraryFactory();
            var installed = await library.DownloadAsync(operation.Model, operation, operation.PauseToken, operation.CancellationToken).ConfigureAwait(false);
            LogCompleted(_logger, operation.Name, installed.Size, installed.Resumed);
            operation.Finish(ModelDownloadStatus.Completed, installed);
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            LogCancelled(_logger, operation.Name);
            operation.Finish(ModelDownloadStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Surfaced on the model's row; the partial file is kept unless verification deleted it.
            LogFailed(_logger, exception, operation.Name);
            operation.Finish(ModelDownloadStatus.Failed, error: exception);
        }
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "Downloading model {Model} ({Size} bytes)")]
    private static partial void LogStarted(ILogger logger, string model, long size);

    [LoggerMessage(EventId = 401, Level = LogLevel.Information, Message = "Installed model {Model} ({Size} bytes, resumed: {Resumed})")]
    private static partial void LogCompleted(ILogger logger, string model, long size, bool resumed);

    [LoggerMessage(EventId = 402, Level = LogLevel.Information, Message = "Download of model {Model} cancelled; the partial file is kept")]
    private static partial void LogCancelled(ILogger logger, string model);

    [LoggerMessage(EventId = 403, Level = LogLevel.Error, Message = "Download of model {Model} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string model);
}
