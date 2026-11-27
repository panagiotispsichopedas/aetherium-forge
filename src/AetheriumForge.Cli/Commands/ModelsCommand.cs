using System.CommandLine;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Projects;

namespace AetheriumForge.Cli.Commands;

/// <summary>
/// <c>models</c>: lists the installed upscaling models, or explains how to get them.
/// </summary>
internal sealed class ModelsCommand
{
    /// <summary>The GitHub release that hosts the converted models.</summary>
    public const string ModelsRelease = "models-v1";

    /// <summary>The repository that hosts the models release.</summary>
    public const string Repository = "panagiotispsichopedas/aetherium-forge";

    private readonly GlobalOptions _globals;

    /// <summary>Initializes a new instance of the <see cref="ModelsCommand"/> class.</summary>
    /// <param name="globals">The global options.</param>
    public ModelsCommand(GlobalOptions globals)
    {
        ArgumentNullException.ThrowIfNull(globals);

        _globals = globals;
        Command = new Command("models", "List the installed upscaling models.") { Models };
        Command.SetAction((parseResult, ct) => CliRunner.RunAsync(parseResult, _globals, session => Task.FromResult(Run(parseResult, session)), ct));
    }

    /// <summary>Gets the command.</summary>
    public Command Command { get; }

    /// <summary>Gets <c>--models</c>.</summary>
    public Option<string?> Models { get; } = new("--models") { Description = $"Models folder. Default: {ModelCatalog.DefaultModelsFolder}.", HelpName = "dir" };

    /// <summary>The command that downloads the published models into <paramref name="modelsFolder"/>.</summary>
    /// <param name="modelsFolder">The models folder.</param>
    /// <returns>A <c>gh release download</c> command line.</returns>
    public static string DownloadHint(string modelsFolder) => $"gh release download {ModelsRelease} --repo {Repository} --dir \"{modelsFolder}\"";

    private int Run(ParseResult parseResult, CliSession session)
    {
        session.AttachLog(null, null);
        var output = session.Out;
        var folder = Path.GetFullPath(parseResult.GetValue(Models) ?? ModelCatalog.DefaultModelsFolder);
        var models = ModelCatalog.Discover(folder);

        output.WriteLine($"Models folder: {folder}");
        if (models.Count == 0)
        {
            output.WriteLine("No models installed.");
            output.WriteLine("Download the published models (all of them, or add --pattern \"realesr-general-x4v3*\" for the compact one):");
            output.WriteLine($"  {DownloadHint(folder)}");
            output.WriteLine("In-app downloads come with the next milestone.");
            return ExitCodes.Ok;
        }

        var nameWidth = Math.Max(4, models.Max(m => m.Name.Length));
        output.WriteLine();
        output.WriteLine($"  {"Name".PadRight(nameWidth)}  Scale  Prec.  {"Size",9}  Licence       Description");
        foreach (var model in models)
        {
            var size = File.Exists(model.Path) ? ProjectLocator.FormatBytes(new FileInfo(model.Path).Length) : "?";
            var scale = model.Scale is { } s ? $"x{s}" : "?";
            output.WriteLine($"  {model.Name.PadRight(nameWidth)}  {scale,-5}  {model.Precision,-5}  {size,9}  {(model.Licence ?? "-"),-12}  {model.DisplayName}");
        }

        return ExitCodes.Ok;
    }
}
