using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Serilog setup: a file log (Information, or Debug with <c>--verbose</c>) and console output on stderr whose level can be raised to
/// Warning while the live view is drawing. The file is chosen once the project is known (<see cref="AttachFile"/>); events logged
/// before that are buffered and written to the file when it is attached.
/// </summary>
internal sealed class CliLogging : IDisposable
{
    private readonly DeferredFileSink _file;

    private CliLogging(Logger logger, LoggingLevelSwitch consoleLevel, DeferredFileSink file)
    {
        Logger = logger;
        ConsoleLevel = consoleLevel;
        _file = file;
    }

    /// <summary>Gets the root logger.</summary>
    public Logger Logger { get; }

    /// <summary>Gets the console sink level.</summary>
    public LoggingLevelSwitch ConsoleLevel { get; }

    /// <summary>Gets the attached log file, or <see langword="null"/>.</summary>
    public string? LogFile => _file.Path;

    /// <summary>Gets the folder of the fallback log used when no project is involved.</summary>
    public static string FallbackLogFolder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "logs");

    /// <summary>
    /// Creates the logger.
    /// </summary>
    /// <param name="verbose">Debug level in the file and Information on the console.</param>
    /// <param name="logFile">The <c>--log</c> file, attached immediately; <see langword="null"/> waits for <see cref="AttachFile"/>.</param>
    /// <returns>The logging setup; dispose it to flush.</returns>
    public static CliLogging Create(bool verbose, string? logFile)
    {
        var consoleLevel = new LoggingLevelSwitch(verbose ? LogEventLevel.Information : LogEventLevel.Warning);
        var file = new DeferredFileSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Session", Guid.NewGuid().ToString("N")[..8])
            .WriteTo.Console(
                levelSwitch: consoleLevel,
                outputTemplate: "{Level:u3} {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose,
                formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Sink(file)
            .CreateLogger();

        var logging = new CliLogging(logger, consoleLevel, file);
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            logging.AttachFile(logFile);
        }

        return logging;
    }

    /// <summary>
    /// Starts writing to <paramref name="path"/> (unless a file is already attached, for example from <c>--log</c>) and flushes the buffer.
    /// </summary>
    /// <param name="path">The log file; its folder is created.</param>
    public void AttachFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _file.Attach(path);
    }

    /// <summary>Raises the console level to at least Warning until the returned scope is disposed.</summary>
    /// <returns>A scope that restores the previous level.</returns>
    public IDisposable QuietConsole()
    {
        var previous = ConsoleLevel.MinimumLevel;
        if (previous < LogEventLevel.Warning)
        {
            ConsoleLevel.MinimumLevel = LogEventLevel.Warning;
        }

        return new Restore(this, previous);
    }

    /// <summary>Sends the next events to the file only (the console shows nothing below Fatal) until the scope is disposed.</summary>
    /// <returns>A scope that restores the previous level.</returns>
    public IDisposable FileOnly()
    {
        var previous = ConsoleLevel.MinimumLevel;
        ConsoleLevel.MinimumLevel = LogEventLevel.Fatal;
        return new Restore(this, previous);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Logger.Dispose();
        _file.Dispose();
    }

    private sealed class Restore(CliLogging owner, LogEventLevel previous) : IDisposable
    {
        public void Dispose() => owner.ConsoleLevel.MinimumLevel = previous;
    }

    /// <summary>Buffers events until a file is attached; then writes them, and everything after, to that file.</summary>
    private sealed class DeferredFileSink : ILogEventSink, IDisposable
    {
        private const int MaxBuffered = 20_000;

        private readonly Lock _gate = new();
        private readonly List<LogEvent> _buffer = [];
        private Logger? _file;

        public string? Path { get; private set; }

        public void Emit(LogEvent logEvent)
        {
            lock (_gate)
            {
                if (_file is not null)
                {
                    _file.Write(logEvent);
                }
                else if (_buffer.Count < MaxBuffered)
                {
                    _buffer.Add(logEvent);
                }
            }
        }

        public void Attach(string path)
        {
            lock (_gate)
            {
                AttachCore(path);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_file is null && _buffer.Exists(e => e.Level >= LogEventLevel.Warning))
                {
                    // Nothing chose a log file (the command failed early): keep the warnings and errors in the fallback log.
                    AttachCore(System.IO.Path.Combine(FallbackLogFolder, $"aetherium-forge-{DateTime.Now:yyyyMMdd}.log"));
                }

                _file?.Dispose();
                _file = null;
                _buffer.Clear();
            }
        }

        private void AttachCore(string path)
        {
            if (_file is not null)
            {
                return;
            }

            var full = System.IO.Path.GetFullPath(path);
            var folder = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _file = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    full,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Session}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    shared: true,
                    formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();
            Path = full;
            foreach (var buffered in _buffer)
            {
                _file.Write(buffered);
            }

            _buffer.Clear();
        }
    }
}
