using System.Globalization;
using System.Text;

namespace AetheriumForge.Cli;

/// <summary>
/// Entry point of <c>aetherium-forge</c>.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        // Numbers, sizes and dates print the same everywhere (and in logs and bug reports).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // Progress bars and arrows need UTF-8; the Windows console defaults to the OEM code page.
        if (!Console.IsOutputRedirected)
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

        return new CliApp().InvokeAsync(args);
    }
}
