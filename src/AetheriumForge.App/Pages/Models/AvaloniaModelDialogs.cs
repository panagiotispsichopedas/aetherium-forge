using AetheriumForge.App.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AetheriumForge.App.Pages.Models;

/// <summary>
/// <see cref="IModelDialogs"/> as modal windows over the main window. Must be called on the UI thread.
/// </summary>
public sealed class AvaloniaModelDialogs : IModelDialogs
{
    /// <inheritdoc />
    public async Task<bool> ConfirmDownloadAsync(IReadOnlyList<ModelDownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var total = items.Sum(item => item.Size);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text($"Download {(items.Count == 1 ? items[0].DisplayName : $"{items.Count} models")} ({ModelFormatting.Bytes(total)}) from the project's GitHub release?"));
        body.Children.Add(ItemsPanel(items));
        body.Children.Add(Text("Each file is checked against its published SHA-256 before it is installed.", 0.7));
        return await ShowAsync("Download model", body, ("Download", true), ("Cancel", false));
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmDeleteAsync(string displayName, long? partialBytes)
    {
        var body = Text(partialBytes is { } partial
            ? $"Delete {displayName}? Its .onnx file, sidecar and partial download ({ModelFormatting.Bytes(partial)}) are removed from the models folder."
            : $"Delete {displayName}? Its .onnx file and sidecar are removed from the models folder.");
        return await ShowAsync("Delete model", body, ("Delete", true), ("Cancel", false));
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmDiscardPartialAsync(string displayName, long partialBytes)
    {
        var body = Text($"Discard the partial download of {displayName} ({ModelFormatting.Bytes(partialBytes)})? The next download starts from the beginning.");
        return await ShowAsync("Discard partial download", body, ("Discard", true), ("Cancel", false));
    }

    /// <inheritdoc />
    public async Task<ModelFirstRunChoice> ShowFirstRunAsync(ModelFirstRunRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text("No upscaling model is installed yet.", fontWeight: FontWeight.SemiBold));
        body.Children.Add(Text(recommendation.AdapterName is null
            ? $"No GPU adapter was found, so the compact model is recommended: {recommendation.Family}."
            : $"Your GPU ({recommendation.AdapterName}) is {recommendation.Tier} tier, so {recommendation.Family} is recommended."));
        body.Children.Add(ItemsPanel(recommendation.Items));
        body.Children.Add(Text($"Total download: {ModelFormatting.Bytes(recommendation.Items.Sum(item => item.Size))}. Each file is checked against its published SHA-256.", 0.7));
        return await ShowAsync(
            "Welcome to Aetherium Forge",
            body,
            ("Download recommended", ModelFirstRunChoice.DownloadRecommended),
            ("Choose…", ModelFirstRunChoice.Choose),
            ("Later", ModelFirstRunChoice.Later));
    }

    /// <inheritdoc />
    public async Task<bool> OfferCalibrationAsync(string family)
    {
        var body = Text($"{family} is installed. Calibrate it for this GPU now? It takes about half a minute; close games and other GPU-heavy apps first.");
        return await ShowAsync("Calibrate GPU", body, ("Calibrate now", true), ("Later", false));
    }

    /// <inheritdoc />
    public async Task<string?> PickModelFileAsync()
    {
        var owner = await MainWindowAsync();
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a custom ONNX model",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ONNX models") { Patterns = ["*.onnx"] }],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<T> ShowAsync<T>(string title, Control body, params (string Label, T Result)[] buttons)
    {
        var owner = await MainWindowAsync();
        var window = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var result = buttons[^1].Result;
        foreach (var (label, value) in buttons)
        {
            var button = new Button { Content = label, MinWidth = 90 };
            button.Click += (_, _) =>
            {
                result = value;
                window.Close();
            };
            row.Children.Add(button);
        }

        if (row.Children[0] is Button primary)
        {
            primary.Classes.Add("accent");
            primary.IsDefault = true;
        }

        window.Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                WithDock(row, Dock.Bottom),
                new Border { Margin = new Thickness(0, 0, 0, 16), Child = body },
            },
        };
        await window.ShowDialog(owner);
        return result;
    }

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static StackPanel ItemsPanel(IReadOnlyList<ModelDownloadItem> items)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var item in items)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new TextBlock { Text = $"• {item.DisplayName}: {ModelFormatting.Bytes(item.Size)}, licence", VerticalAlignment = VerticalAlignment.Center });
            if (item.LicenceUrl is { } url)
            {
                line.Children.Add(new HyperlinkButton { Content = item.Licence ?? "licence", NavigateUri = url, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                line.Children.Add(new TextBlock { Text = item.Licence ?? "not stated", VerticalAlignment = VerticalAlignment.Center });
            }

            panel.Children.Add(line);
        }

        return panel;
    }

    private static TextBlock Text(string text, double opacity = 1, FontWeight fontWeight = FontWeight.Normal) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity, FontWeight = fontWeight };

    private static async Task<Window> MainWindowAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            throw new InvalidOperationException("The main window is not available.");
        }

        if (!main.IsVisible)
        {
            // The first-run prompt can be raised while the main window is still opening; a dialog needs a visible owner.
            var opened = new TaskCompletionSource();
            main.Opened += (_, _) => opened.TrySetResult();
            if (!main.IsVisible)
            {
                await opened.Task;
            }
        }

        return main;
    }
}
