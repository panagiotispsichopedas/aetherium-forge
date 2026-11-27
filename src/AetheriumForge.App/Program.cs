using Avalonia;

namespace AetheriumForge.App;

/// <summary>
/// Entry point of the desktop app.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the Avalonia app; also used by the designer.</summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
