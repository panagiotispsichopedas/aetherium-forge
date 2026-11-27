using AetheriumForge.App.Pages.Rules;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.App.Rules;

/// <summary>A clock whose timers fire only when the test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.Due = _now + dueTime;
                _timers.Add(timer);
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Schedule(this, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeProjectSession(ProjectInfo? current, JobStore? store) : IProjectSession
{
    public event EventHandler? Changed;

    public event EventHandler? JobsChanged;

    public ProjectInfo? Current { get; } = current;

    public JobStore? Store { get; } = store;

    public int JobsChangedCount { get; private set; }

    public Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void NotifyJobsChanged()
    {
        JobsChangedCount++;
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeNavigation : INavigationService
{
    public event EventHandler<AppPage>? Navigated;

    public AppPage Current { get; private set; } = AppPage.Scan;

    public void NavigateTo(AppPage page)
    {
        Current = page;
        Navigated?.Invoke(this, page);
    }
}

internal sealed class FakeRulesPageServices : IRulesPageServices
{
    public List<string> Questions { get; } = [];

    public bool ConfirmAnswer { get; set; }

    /// <summary>Answers used first, in order; <see cref="ConfirmAnswer"/> after they run out.</summary>
    public Queue<bool> Answers { get; } = new();

    /// <summary>Runs while a confirmation is open (simulates something happening behind the dialog).</summary>
    public Action? WhileConfirming { get; set; }

    public Exception? OpenException { get; set; }

    public Exception? SaveException { get; set; }

    public string? JsonToOpen { get; set; }

    public string? SavedJson { get; private set; }

    public IReadOnlyDictionary<string, double> Timings { get; set; } = new Dictionary<string, double>();

    public IReadOnlyList<string> DiscoverModels() => ["model-x"];

    public Task<IReadOnlyDictionary<string, double>> LoadSecondsPerMegapixelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Timings);

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Questions.Add(message);
        WhileConfirming?.Invoke();
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : ConfirmAnswer);
    }

    public Task<string?> OpenJsonAsync() => OpenException is { } ex ? Task.FromException<string?>(ex) : Task.FromResult(JsonToOpen);

    public Task<bool> SaveJsonAsync(string suggestedFileName, string json)
    {
        if (SaveException is { } ex)
        {
            return Task.FromException<bool>(ex);
        }

        SavedJson = json;
        return Task.FromResult(true);
    }

    public void PostToUi(Action action) => action();
}

internal sealed class FakeForgeRunner : IForgeRunner
{
    public event EventHandler<PipelineProgress>? ProgressChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    public PipelineState State { get; private set; }

    public PipelineProgress? LastProgress => null;

    public void SetState(PipelineState state)
    {
        State = state;
        RunStateChanged?.Invoke(this, null);
    }

    public Task<PipelineRunSummary> StartAsync(ForgeRunRequest request) => throw new NotSupportedException();

    public void Pause() => throw new NotSupportedException();

    public void Unpause() => throw new NotSupportedException();

    public void RequestStop() => throw new NotSupportedException();
}

internal sealed class SingleServiceProvider(IForgeRunner? runner) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType == typeof(IForgeRunner) ? runner : null;
}
