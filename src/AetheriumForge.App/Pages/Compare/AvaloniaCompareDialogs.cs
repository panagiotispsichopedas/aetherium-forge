using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// <see cref="ICompareDialogs"/> with a small modal Avalonia window over the main window. No is the default button.
/// </summary>
public sealed class AvaloniaCompareDialogs : ICompareDialogs
{
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var answer = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
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
            MaxWidth = 460,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yes, no },
                },
            },
        };
        dialog.Opened += (_, _) => no.Focus(NavigationMethod.Tab);

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return answer;
    }
}
