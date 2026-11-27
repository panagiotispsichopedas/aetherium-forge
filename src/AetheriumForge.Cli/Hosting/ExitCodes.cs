namespace AetheriumForge.Cli.Hosting;

/// <summary>
/// Process exit codes of <c>aetherium-forge</c>.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The command completed.</summary>
    public const int Ok = 0;

    /// <summary>The command failed (bad input, missing tool, I/O error, ...).</summary>
    public const int Error = 1;

    /// <summary>The user stopped the run (S key or Ctrl+C) before it finished.</summary>
    public const int Stopped = 2;
}
