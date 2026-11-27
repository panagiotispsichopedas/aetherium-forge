using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Shell;

/// <summary>
/// Stand-in view model for a page that is not implemented yet. Each M4 session replaces its page's placeholder registration in
/// <see cref="AppHost"/> with its real view model.
/// </summary>
/// <param name="title">The page title.</param>
public sealed partial class PagePlaceholderViewModel(string title) : ObservableObject
{
    /// <summary>Gets the page title.</summary>
    public string Title { get; } = title;
}
