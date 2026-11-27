using AetheriumForge.App.Services;

namespace AetheriumForge.App.Shell;

/// <summary>
/// Default <see cref="INavigationService"/>: keeps the current page and raises <see cref="Navigated"/>.
/// </summary>
public sealed class NavigationService : INavigationService
{
    /// <inheritdoc />
    public event EventHandler<AppPage>? Navigated;

    /// <inheritdoc />
    public AppPage Current { get; private set; } = AppPage.Scan;

    /// <inheritdoc />
    public void NavigateTo(AppPage page)
    {
        if (Current == page)
        {
            return;
        }

        Current = page;
        Navigated?.Invoke(this, page);
    }
}
