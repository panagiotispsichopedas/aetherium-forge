namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// A user-facing error: the message is printed as is (no stack trace) and the process exits with <see cref="ExitCodes.Error"/>.
/// </summary>
internal sealed class CliException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CliException"/> class.</summary>
    public CliException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CliException"/> class.</summary>
    /// <param name="message">What went wrong and, ideally, how to fix it.</param>
    public CliException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CliException"/> class.</summary>
    /// <param name="message">What went wrong and, ideally, how to fix it.</param>
    /// <param name="innerException">The underlying error, written to the log.</param>
    public CliException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
