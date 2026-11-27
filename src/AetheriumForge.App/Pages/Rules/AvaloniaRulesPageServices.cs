using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// Desktop implementation of <see cref="IRulesPageServices"/>: Avalonia dialogs and <see cref="IStorageProvider"/> pickers on the main window,
/// <see cref="ModelCatalog"/> for models and <see cref="GpuProfileStore"/> for timings.
/// </summary>
/// <param name="logger">Logs failures to read the optional GPU profiles.</param>
public sealed class AvaloniaRulesPageServices(ILogger<AvaloniaRulesPageServices> logger) : IRulesPageServices
{
    private static readonly FilePickerFileType[] _jsonTypes = [new("Rules JSON") { Patterns = ["*.json"] }];

    private static Window? MainWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <inheritdoc />
    public IReadOnlyList<string> DiscoverModels() => [.. ModelCatalog.Discover(ModelCatalog.DefaultModelsFolder).Select(m => m.Name)];

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, double>> LoadSecondsPerMegapixelAsync(CancellationToken cancellationToken = default)
    {
        using var store = new GpuProfileStore();
        try
        {
            var profiles = await store.ListAsync(cancellationToken).ConfigureAwait(false);

            // One family may be calibrated on several adapters; the fastest one is the best guess for the run.
            return profiles
                .Where(p => p.SecondsPerMegapixel > 0)
                .GroupBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(p => p.SecondsPerMegapixel), StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException ex)
        {
            RulesLog.GpuProfilesUnreadable(logger, ex);
            return new Dictionary<string, double>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = MainWindow;
        if (owner is null)
        {
            return false;
        }

        var answer = false;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = "Yes", MinWidth = 80 };
        var no = new Button { Content = "No", MinWidth = 80, IsDefault = true, IsCancel = true };
        yes.Click += (_, _) =>
        {
            answer = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yes, no } },
            },
        };
        dialog.Opened += (_, _) => no.Focus(NavigationMethod.Tab);

        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <inheritdoc />
    public async Task<string?> OpenJsonAsync()
    {
        if (MainWindow?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import rules", AllowMultiple = false, FileTypeFilter = _jsonTypes });
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <inheritdoc />
    public async Task<bool> SaveJsonAsync(string suggestedFileName, string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (MainWindow?.StorageProvider is not { } storage)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export rules",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = _jsonTypes,
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }

        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
        return true;
    }

    /// <inheritdoc />
    public void PostToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
