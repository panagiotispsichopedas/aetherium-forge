using AetheriumForge.Core.Concurrency;

namespace AetheriumForge.Core.Tests.Concurrency;

public sealed class SingleInstanceTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _mutexName = $@"Local\AetheriumForge.Tests.SingleInstance.{Guid.NewGuid():N}";
    private readonly string _pipeName = $"AetheriumForge.Tests.Activate.{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_NoOtherInstance_IsFirst()
    {
        var first = SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var instance);
        instance?.Dispose();

        Assert.True(first);
    }

    [Fact]
    public void TryAcquire_FirstInstanceRunning_IsNotFirst()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        using var running = first;

        var second = SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var other);

        Assert.Equal((false, (SingleInstance?)null), (second, other));
    }

    [Fact]
    public void Dispose_FirstInstance_AllowsNewFirstInstance()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        first!.Dispose();

        var again = SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var instance);
        instance?.Dispose();

        Assert.True(again);
    }

    [Fact]
    public async Task TrySignalPrimary_Listening_RaisesActivationWithArgs()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        using var running = first!;
        var raised = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        running.ActivationRequested += (_, e) => raised.TrySetResult(e.Args);
        running.StartListening();

        var acknowledged = await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, ["--project", @"D:\p q"], SignalTimeout, null, null), TestContext.Current.CancellationToken);
        var args = await raised.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        Assert.Equal((true, @"--project|D:\p q"), (acknowledged, string.Join('|', args)));
    }

    [Fact]
    public async Task TrySignalPrimary_Listening_ReportsPrimaryPidBeforeActivation()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        using var running = first!;
        running.StartListening();
        var pid = 0;

        await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, [], SignalTimeout, p => pid = p, null), TestContext.Current.CancellationToken);

        Assert.Equal(Environment.ProcessId, pid);
    }

    [Fact]
    public async Task TrySignalPrimary_RepeatedRequests_AllRaised()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        using var running = first!;
        var count = 0;
        running.ActivationRequested += (_, _) => Interlocked.Increment(ref count);
        running.StartListening();

        for (var i = 0; i < 3; i++)
        {
            await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, [], SignalTimeout, null, null), TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(() => Volatile.Read(ref count) == 3);
        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public async Task TrySignalPrimary_HandlerThrows_ListenerKeepsServing()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        using var running = first!;
        running.ActivationRequested += (_, _) => throw new InvalidOperationException("boom");
        running.StartListening();
        await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, [], SignalTimeout, null, null), TestContext.Current.CancellationToken);

        var again = await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, [], SignalTimeout, null, null), TestContext.Current.CancellationToken);

        Assert.True(again);
    }

    [Fact]
    public void TrySignalPrimary_NoListener_ReturnsFalse()
    {
        var acknowledged = SingleInstance.TrySignalPrimary(_pipeName, [], TimeSpan.FromMilliseconds(300), null, null);

        Assert.False(acknowledged);
    }

    [Fact]
    public async Task Dispose_Listening_StopsListener()
    {
        SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var first);
        first!.StartListening();

        first.Dispose();
        var acknowledged = await Task.Run(() => SingleInstance.TrySignalPrimary(_pipeName, [], TimeSpan.FromMilliseconds(300), null, null), TestContext.Current.CancellationToken);

        Assert.Equal((false, false), (first.IsListening, acknowledged));
    }

    [Fact]
    public async Task TryAcquire_RunningInChildProcess_IsNotFirst()
    {
        using var child = await ChildHolder.CreateMutexAsync(_mutexName);

        var first = SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var instance);
        instance?.Dispose();

        Assert.False(first);
    }

    [Fact]
    public async Task TryAcquire_ChildInstanceExited_IsFirst()
    {
        using var child = await ChildHolder.CreateMutexAsync(_mutexName);
        await child.KillAsync();

        var first = SingleInstance.TryAcquire(_mutexName, _pipeName, null, out var instance);
        instance?.Dispose();

        Assert.True(first);
    }

    [Fact]
    public void DefaultPipeName_CurrentUser_EndsWithSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

        Assert.Equal($"AetheriumForge.App.Activate.{identity.User!.Value}", SingleInstance.DefaultPipeName);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
