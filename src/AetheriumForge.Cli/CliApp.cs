using System.CommandLine;
using AetheriumForge.Cli.Commands;
using AetheriumForge.Cli.Hosting;

namespace AetheriumForge.Cli;

/// <summary>
/// The command tree of <c>aetherium-forge</c>: <c>scan</c>, <c>forge</c>, <c>status</c> and <c>models</c>, plus the global
/// <c>--verbose</c> and <c>--log</c> options.
/// </summary>
internal sealed class CliApp
{
    /// <summary>Initializes a new instance of the <see cref="CliApp"/> class.</summary>
    public CliApp()
    {
        Globals = new GlobalOptions();
        Scan = new ScanCommand(Globals);
        Forge = new ForgeCommand(Globals);
        Status = new StatusCommand(Globals);
        Models = new ModelsCommand(Globals);

        Root = new RootCommand("Aetherium Forge: AI-upscale the Skyrim SE textures that no installed mod replaces.")
        {
            Globals.Verbose,
            Globals.Log,
            Scan.Command,
            Forge.Command,
            Status.Command,
            Models.Command,
        };
    }

    /// <summary>Gets the global options.</summary>
    public GlobalOptions Globals { get; }

    /// <summary>Gets <c>scan</c>.</summary>
    public ScanCommand Scan { get; }

    /// <summary>Gets <c>forge</c>.</summary>
    public ForgeCommand Forge { get; }

    /// <summary>Gets <c>status</c>.</summary>
    public StatusCommand Status { get; }

    /// <summary>Gets <c>models</c>.</summary>
    public ModelsCommand Models { get; }

    /// <summary>Gets the root command.</summary>
    public RootCommand Root { get; }

    /// <summary>Parses and runs a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    public Task<int> InvokeAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Ctrl+C is handled by ConsoleCancellation (first press: graceful stop; second: exit), not by System.CommandLine.
        var configuration = new InvocationConfiguration { ProcessTerminationTimeout = null };
        return Root.Parse(args).InvokeAsync(configuration);
    }
}
