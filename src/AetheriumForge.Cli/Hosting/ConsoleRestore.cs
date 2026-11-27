namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Tracks console changes the CLI makes (hidden cursor) so they can be undone on every exit path, including a forced exit by a second Ctrl+C.
/// </summary>
internal static class ConsoleRestore
{
    private static int _cursorHidden;

    /// <summary>Gets a value indicating whether the CLI hid the cursor and has not shown it again.</summary>
    public static bool CursorHidden => Volatile.Read(ref _cursorHidden) == 1;

    /// <summary>Hides the cursor and remembers it.</summary>
    public static void HideCursor()
    {
        Console.CursorVisible = false;
        Volatile.Write(ref _cursorHidden, 1);
    }

    /// <summary>Shows the cursor again if the CLI hid it.</summary>
    public static void ShowCursor()
    {
        if (Interlocked.Exchange(ref _cursorHidden, 0) == 1)
        {
            Console.CursorVisible = true;
        }
    }

    /// <summary>Restores the cursor and the colours. Safe to call from the Ctrl+C handler; console errors are reported to the caller.</summary>
    public static void RestoreAll()
    {
        ShowCursor();
        Console.ResetColor();
    }
}
