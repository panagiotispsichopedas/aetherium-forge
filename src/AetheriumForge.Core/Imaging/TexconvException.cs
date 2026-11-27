namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Thrown when a texconv run fails: non-zero exit code, a reported failure, a missing output file, or a timeout.
/// </summary>
public sealed class TexconvException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TexconvException"/> class.</summary>
    public TexconvException()
    {
        OutputTail = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="TexconvException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public TexconvException(string message)
        : base(message)
    {
        OutputTail = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="TexconvException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public TexconvException(string message, Exception innerException)
        : base(message, innerException)
    {
        OutputTail = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="TexconvException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="exitCode">The process exit code, or <see langword="null"/> when the process was killed.</param>
    /// <param name="outputTail">The last part of the combined standard output and standard error.</param>
    /// <param name="timedOut"><see langword="true"/> when the process was killed because it exceeded the timeout.</param>
    public TexconvException(string message, int? exitCode, string outputTail, bool timedOut = false)
        : base(message)
    {
        ExitCode = exitCode;
        OutputTail = outputTail ?? string.Empty;
        TimedOut = timedOut;
    }

    /// <summary>Gets the process exit code, or <see langword="null"/> when the process did not exit on its own.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the last part of texconv's combined standard output and standard error.</summary>
    public string OutputTail { get; }

    /// <summary>Gets a value indicating whether the process was killed because it exceeded <see cref="TexconvOptions.Timeout"/>.</summary>
    public bool TimedOut { get; }
}
