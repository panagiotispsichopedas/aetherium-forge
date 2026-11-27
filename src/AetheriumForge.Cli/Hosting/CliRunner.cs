using System.CommandLine;
using System.Text.Json;
using AetheriumForge.Core.Imaging;
using Serilog;

namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// The options every command accepts (<c>--verbose</c>, <c>--log</c>).
/// </summary>
internal sealed class GlobalOptions
{
    /// <summary>Gets <c>--verbose</c>: Debug in the log file, Information on the console.</summary>
    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Write more detail: Debug to the log file and Information to the console.",
        Recursive = true,
    };

    /// <summary>Gets <c>--log</c>: the log file.</summary>
    public Option<string?> Log { get; } = new("--log")
    {
        Description = "Log file. Default: <project>\\logs\\aetherium-forge-<date>.log.",
        HelpName = "file",
        Recursive = true,
    };
}

/// <summary>
/// What a command body receives: the output writers, the logging setup and the stop token.
/// </summary>
/// <param name="Out">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="Logging">Logging; attach the project log file once the project is known.</param>
/// <param name="Cancellation">Ctrl+C handling; its token is the stop token.</param>
internal sealed record CliSession(TextWriter Out, TextWriter Error, CliLogging Logging, ConsoleCancellation Cancellation)
{
    /// <summary>Starts the project log (unless <c>--log</c> chose another file).</summary>
    /// <param name="project">The project.</param>
    public void AttachProjectLog(ProjectLayout project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Logging.AttachFile(project.DefaultLogFile(DateOnly.FromDateTime(DateTime.Now)));
    }
}

/// <summary>
/// Runs a command body with logging, Ctrl+C handling and error reporting: <see cref="CliException"/> prints its message; any other
/// error prints a one-line summary and is logged with its stack trace.
/// </summary>
internal static class CliRunner
{
    /// <summary>Runs <paramref name="body"/>.</summary>
    /// <param name="parseResult">The parse result (for the global options).</param>
    /// <param name="globals">The global options.</param>
    /// <param name="body">The command.</param>
    /// <param name="cancellationToken">The invocation token.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(ParseResult parseResult, GlobalOptions globals, Func<CliSession, Task<int>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(body);

        var output = parseResult.InvocationConfiguration.Output;
        var error = parseResult.InvocationConfiguration.Error;
        using var logging = CliLogging.Create(parseResult.GetValue(globals.Verbose), parseResult.GetValue(globals.Log));
        using var cancellation = new ConsoleCancellation(error, cancellationToken);
        var log = logging.Logger.ForContext("SourceContext", "AetheriumForge.Cli");
        log.Information("aetherium-forge {Arguments}", string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));

        try
        {
            return await body(new CliSession(output, error, logging, cancellation)).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            using (logging.FileOnly())
            {
                log.Error(ex, "Command failed: {Message}", ex.Message);
            }

            WriteError(error, ex.Message);
            return ExitCodes.Error;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            log.Warning("Cancelled by the user");
            error.WriteLine("Cancelled.");
            return ExitCodes.Stopped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or TexconvException
                                       or InvalidOperationException or KeyNotFoundException or NotSupportedException or ArgumentException
                                       or Microsoft.Data.Sqlite.SqliteException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            using (logging.FileOnly())
            {
                log.Error(ex, "Command failed");
            }

            WriteError(error, $"{ex.Message}{(logging.LogFile is { } file ? $"{Environment.NewLine}Details: {file}" : string.Empty)}");
            return ExitCodes.Error;
        }
    }

    /// <summary>Writes an error message in red when the console supports colour.</summary>
    /// <param name="error">Standard error.</param>
    /// <param name="message">The message.</param>
    public static void WriteError(TextWriter error, string message)
    {
        ArgumentNullException.ThrowIfNull(error);

        var colour = !Console.IsErrorRedirected && ReferenceEquals(error, Console.Error);
        if (colour)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }

        error.WriteLine($"error: {message}");
        if (colour)
        {
            Console.ResetColor();
        }
    }
}
