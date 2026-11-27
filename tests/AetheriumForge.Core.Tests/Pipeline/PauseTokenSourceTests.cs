using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Core.Tests.Pipeline;

public sealed class PauseTokenSourceTests
{
    [Fact]
    public void WaitWhilePausedAsync_NotPaused_CompletesImmediately()
    {
        var source = new PauseTokenSource();

        var wait = source.Token.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public void WaitWhilePausedAsync_Paused_DoesNotComplete()
    {
        var source = new PauseTokenSource();
        source.Pause();

        var wait = source.Token.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_Resumed_Completes()
    {
        // Arrange
        var source = new PauseTokenSource();
        source.Pause();
        var wait = source.Token.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        // Act
        source.Resume();

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_CancelledWhilePaused_Throws()
    {
        var source = new PauseTokenSource();
        source.Pause();
        using var cts = new CancellationTokenSource();
        var wait = source.Token.WaitWhilePausedAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void None_IsNeverPaused()
    {
        Assert.False(PauseToken.None.IsPaused);
    }
}
