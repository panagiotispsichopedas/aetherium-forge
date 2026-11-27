using AetheriumForge.App.Pages.Queue;
using AetheriumForge.App.Services;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Queue;

/// <summary>Runs UI work inline.</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>A project session over a temp job store.</summary>
internal sealed class FakeProjectSession : IProjectSession
{
    public event EventHandler? Changed;

    public event EventHandler? JobsChanged;

    public ProjectInfo? Current { get; private set; }

    public JobStore? Store { get; private set; }

    public int NotifyCount { get; private set; }

    public Task OpenAsync(string projectFolder, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void NotifyJobsChanged()
    {
        NotifyCount++;
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Open(string root, JobStore store)
    {
        Current = new ProjectInfo(root, Path.Combine(root, "game"), Path.Combine(root, "game", "Data"), Path.Combine(root, "output"), Path.Combine(root, "work"), ForgeRules.Default);
        Store = store;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        Current = null;
        Store = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A runner whose state the test sets.</summary>
internal sealed class FakeForgeRunner : IForgeRunner
{
    public event EventHandler<PipelineProgress>? ProgressChanged;

    public event EventHandler<PipelineRunSummary?>? RunStateChanged;

    public PipelineState State { get; private set; }

    public PipelineProgress? LastProgress => null;

    public Task<PipelineRunSummary> StartAsync(ForgeRunRequest request) => throw new NotSupportedException();

    public void Pause() => throw new NotSupportedException();

    public void Unpause() => throw new NotSupportedException();

    public void RequestStop() => throw new NotSupportedException();

    public void SetState(PipelineState state)
    {
        State = state;
        RunStateChanged?.Invoke(this, null);
        _ = ProgressChanged;
    }
}

/// <summary>Records navigation.</summary>
internal sealed class FakeNavigationService : INavigationService
{
    public event EventHandler<AppPage>? Navigated;

    public AppPage Current { get; private set; } = AppPage.Queue;

    public void NavigateTo(AppPage page)
    {
        Current = page;
        Navigated?.Invoke(this, page);
    }
}

/// <summary>A clock whose timers fire only when the test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var end = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
            {
                next = _timers.Where(timer => timer.DueAt is { } due && due <= end).MinBy(timer => timer.DueAt);
            }

            if (next is null)
            {
                break;
            }

            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = end;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;

            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;
            callback(state);
        }

        public void Dispose()
        {
            DueAt = null;
            lock (owner._timers)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
