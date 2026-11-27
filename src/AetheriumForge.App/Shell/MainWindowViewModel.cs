using AetheriumForge.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetheriumForge.App.Shell;

/// <summary>
/// Main window: left navigation and the current page's view model.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IReadOnlyDictionary<AppPage, object> _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="navigation">Navigation.</param>
    /// <param name="pages">The view model of each page.</param>
    public MainWindowViewModel(INavigationService navigation, IReadOnlyDictionary<AppPage, object> pages)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(pages);

        _navigation = navigation;
        _pages = pages;
        _currentPage = pages[navigation.Current];
        _selectedPage = navigation.Current;
        navigation.Navigated += (_, page) =>
        {
            SelectedPage = page;
            CurrentPage = _pages[page];
        };
    }

    /// <summary>Gets the navigation entries, in display order.</summary>
    public IReadOnlyList<AppPage> Pages { get; } = Enum.GetValues<AppPage>();

    /// <summary>Gets or sets the selected navigation entry.</summary>
    [ObservableProperty]
    private AppPage _selectedPage;

    /// <summary>Gets the current page's view model.</summary>
    [ObservableProperty]
    private object _currentPage;

    /// <summary>Navigates to a page.</summary>
    /// <param name="page">The page.</param>
    [RelayCommand]
    private void Navigate(AppPage page) => _navigation.NavigateTo(page);

    partial void OnSelectedPageChanged(AppPage value) => _navigation.NavigateTo(value);
}
