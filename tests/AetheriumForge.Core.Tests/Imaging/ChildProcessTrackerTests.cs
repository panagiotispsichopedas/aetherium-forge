using System.Diagnostics;
using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging;

public sealed class ChildProcessTrackerTests
{
    [Fact]
    public async Task KillAll_TrackedRunningProcess_KillsIt()
    {
        // Arrange
        var tracker = new ChildProcessTracker();
        using var process = StartSleeper();
        using var registration = tracker.Track(process);

        // Act
        var killed = tracker.KillAll();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((1, true), (killed, process.HasExited));
    }

    [Fact]
    public void Track_RegistrationDisposed_StopsTracking()
    {
        var tracker = new ChildProcessTracker();
        using var process = StartSleeper();
        var registration = tracker.Track(process);

        registration.Dispose();
        process.Kill(entireProcessTree: true);

        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task KillAll_ProcessAlreadyExited_IsIgnored()
    {
        // Arrange
        var tracker = new ChildProcessTracker();
        using var process = StartSleeper();
        using var registration = tracker.Track(process);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        // Act
        var killed = tracker.KillAll();

        // Assert
        Assert.Equal(0, killed);
    }

    private static Process StartSleeper() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new InvalidOperationException("Could not start the test process.");
}
