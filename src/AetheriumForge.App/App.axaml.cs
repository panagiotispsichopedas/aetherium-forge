using AetheriumForge.App.Shell;
using AetheriumForge.Core.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AetheriumForge.App;

/// <summary>
/// The Avalonia application: builds the DI host (<see cref="AppHost"/>) and shows the main window.
/// </summary>
public sealed partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Attaches the first-instance guard: once the main window exists, activation requests from later launches bring it to the front.
    /// </summary>
    /// <param name="singleInstance">The guard taken in <c>Program.Main</c>; the caller disposes it.</param>
    internal void AttachSingleInstance(SingleInstance singleInstance)
    {
        ArgumentNullException.ThrowIfNull(singleInstance);

        _singleInstance = singleInstance;
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Build();
            _host.Start();
            desktop.MainWindow = _host.Services.GetRequiredService<MainWindow>();
            desktop.ShutdownRequested += (_, _) => _host.Dispose();

            if (_singleInstance is { } singleInstance)
            {
                // Raised on the listener thread: marshal to the UI thread.
                singleInstance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(() => BringToFront(desktop.MainWindow));
                singleInstance.StartListening();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Restores, shows and activates the window, and raises it above other windows.</summary>
    /// <param name="window">The main window.</param>
    private static void BringToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();

        // Activate may only flash the taskbar button; a brief Topmost toggle raises the window regardless.
        if (!window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
    }
}
