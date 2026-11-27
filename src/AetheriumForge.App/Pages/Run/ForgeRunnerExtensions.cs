using AetheriumForge.App.Services;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.App.Pages.Run;

/// <summary>
/// Helpers on <see cref="IForgeRunner"/> for the Run page and the close prompt.
/// </summary>
public static class ForgeRunnerExtensions
{
    /// <summary>Completes when the runner is idle (immediately when no run is active).</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the active run has ended.</returns>
    public static async Task WaitForIdleAsync(this IForgeRunner runner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRunStateChanged(object? sender, PipelineRunSummary? summary)
        {
            if (summary is not null || runner.State == PipelineState.Idle)
            {
                ended.TrySetResult();
            }
        }

        runner.RunStateChanged += OnRunStateChanged;
        try
        {
            // Checked after subscribing, so a run that ends in between is not missed.
            if (runner.State == PipelineState.Idle)
            {
                return;
            }

            await ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runner.RunStateChanged -= OnRunStateChanged;
        }
    }
}
