using AetheriumForge.App.Shell;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AetheriumForge.App;

/// <summary>
/// The Avalonia application: builds the DI host (<see cref="AppHost"/>) and shows the main window.
/// </summary>
public sealed partial class App : Application
{
    private IHost? _host;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Build();
            _host.Start();
            desktop.MainWindow = _host.Services.GetRequiredService<MainWindow>();
            desktop.ShutdownRequested += (_, _) => _host.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
