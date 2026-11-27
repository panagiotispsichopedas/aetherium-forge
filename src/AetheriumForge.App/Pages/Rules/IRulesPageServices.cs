namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// Platform services the Rules page needs: dialogs, file pickers, the model list, GPU timings and UI-thread dispatch.
/// Abstracted so <see cref="RulesViewModel"/> runs without Avalonia in tests.
/// </summary>
public interface IRulesPageServices
{
    /// <summary>Lists the installed model names (for the model picker).</summary>
    /// <returns>The model names.</returns>
    IReadOnlyList<string> DiscoverModels();

    /// <summary>Loads measured seconds per source megapixel per model family (from the GPU profiles).</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>Seconds per megapixel keyed by model family; empty when nothing is calibrated.</returns>
    Task<IReadOnlyDictionary<string, double>> LoadSecondsPerMegapixelAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks a yes/no question. The default answer (Enter, Escape, closing the dialog) is No.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question.</param>
    /// <returns><see langword="true"/> when the user chose Yes.</returns>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Lets the user pick a JSON file and returns its text.</summary>
    /// <returns>The file text, or <see langword="null"/> when cancelled.</returns>
    Task<string?> OpenJsonAsync();

    /// <summary>Lets the user pick a destination and writes <paramref name="json"/> to it.</summary>
    /// <param name="suggestedFileName">The suggested file name.</param>
    /// <param name="json">The text to write.</param>
    /// <returns><see langword="true"/> when written; <see langword="false"/> when cancelled.</returns>
    Task<bool> SaveJsonAsync(string suggestedFileName, string json);

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already there or when there is no UI).</summary>
    /// <param name="action">The action.</param>
    void PostToUi(Action action);
}
