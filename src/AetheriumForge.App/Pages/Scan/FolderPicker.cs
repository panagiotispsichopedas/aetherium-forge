using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>
/// Asks the user for a folder.
/// </summary>
public interface IFolderPicker
{
    /// <summary>Shows a folder picker.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="startFolder">Where the dialog opens, or <see langword="null"/>.</param>
    /// <returns>The chosen local folder, or <see langword="null"/> when the user cancelled.</returns>
    Task<string?> PickFolderAsync(string title, string? startFolder);
}

/// <summary>
/// <see cref="IFolderPicker"/> on the main window's Avalonia <see cref="IStorageProvider"/>.
/// </summary>
public sealed class StorageFolderPicker : IFolderPicker
{
    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
            ?? throw new InvalidOperationException("The main window is not open.");
        var provider = window.StorageProvider;

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(startFolder);
        }

        var folders = await provider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
