using AetheriumForge.App.Pages.Run;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.App.Run;

public sealed class RunStopFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StopAsync_StopTakesLongerThanDelay_OffersForceCloseOnce()
    {
        // Arrange
        var runner = new FakeRunner { State = PipelineState.Running };
        var flow = new RunStopFlow(runner, TimeProvider.System, TimeSpan.FromMilliseconds(50));
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offers = 0;

        // Act
        var stop = flow.StopAsync(() =>
        {
            Interlocked.Increment(ref offers);
            offered.TrySetResult();
        }, Ct);
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        runner.SetState(PipelineState.Idle, new PipelineRunSummary(0, 0, 1, true, TimeSpan.Zero));
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        Assert.Equal((1, 1), (offers, runner.Stops));
    }

    [Fact]
    public async Task StopAsync_StopEndsBeforeDelay_DoesNotOfferForceClose()
    {
        // Arrange
        var runner = new FakeRunner { State = PipelineState.Running };
        var flow = new RunStopFlow(runner, TimeProvider.System, TimeSpan.FromHours(1));
        var offers = 0;

        // Act
        var stop = flow.StopAsync(() => offers++, Ct);
        runner.SetState(PipelineState.Idle, new PipelineRunSummary(0, 0, 1, true, TimeSpan.Zero));
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        Assert.Equal(0, offers);
    }
}
