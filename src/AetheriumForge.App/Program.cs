using System.Runtime.InteropServices;
using AetheriumForge.Core.Concurrency;
using Avalonia;

namespace AetheriumForge.App;

/// <summary>
/// Entry point of the desktop app.
/// </summary>
internal static class Program
{
    /// <summary>How long a second instance waits for the running one to answer.</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        // One app per logon session: a second launch brings the running window to the front and exits.
        if (!SingleInstance.TryAcquire(out var instance))
        {
            return SingleInstance.TrySignalPrimary(args, ActivateTimeout, AllowForeground) ? 0 : 1;
        }

        using (instance)
        {
            return BuildAvaloniaApp()
                .AfterSetup(builder => ((App)builder.Instance!).AttachSingleInstance(instance))
                .StartWithClassicDesktopLifetime(args);
        }
    }

    /// <summary>Builds the Avalonia app; also used by the designer.</summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Lets the running instance take the foreground: Windows only lets the process the user just interacted with (this one) pass that
    /// right on.
    /// </summary>
    /// <param name="processId">The running instance's process id.</param>
    private static void AllowForeground(int processId) => _ = AllowSetForegroundWindow(processId);

    // DllImport rather than LibraryImport: the signature is blittable and LibraryImport needs unsafe code, which only the Native project allows.
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int AllowSetForegroundWindow(int dwProcessId);
}
