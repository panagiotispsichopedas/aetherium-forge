namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Settings for running <c>texconv.exe</c> (IMPLEMENTATION_GUIDE §10).
/// </summary>
public sealed record TexconvOptions
{
    /// <summary>Gets the full path to <c>texconv.exe</c>.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Gets the GPU adapter index passed as <c>-gpu N</c>, or <see langword="null"/> for texconv's default adapter.</summary>
    public int? GpuAdapter { get; init; }

    /// <summary>Gets a value indicating whether BC7 encoding may use the GPU. When <see langword="false"/>, <c>-nogpu</c> is always passed.</summary>
    public bool UseGpu { get; init; } = true;

    /// <summary>Gets the maximum run time of one texconv process before it is killed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets an optional tracker that every started texconv process registers with, so a host can kill them on a forced exit.</summary>
    public ChildProcessTracker? ProcessTracker { get; init; }
}
