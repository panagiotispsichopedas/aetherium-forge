namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// Confirmation prompts of the Compare page.
/// </summary>
public interface ICompareDialogs
{
    /// <summary>Asks a yes/no question; the default (Enter, Escape, closing the window) is No.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The question.</param>
    /// <returns><see langword="true"/> only when the user chose Yes.</returns>
    Task<bool> ConfirmAsync(string title, string message);
}
