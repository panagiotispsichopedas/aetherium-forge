namespace AetheriumForge.Core.Discovery;

/// <summary>
/// Estimates disk space for a project and ranks fixed drives for the working folder (IMPLEMENTATION_GUIDE §4.3).
/// </summary>
public static class DriveAdvisor
{
    /// <summary>Bytes per pixel of an 8-bit RGBA working image.</summary>
    private const long BytesPerWorkingPixel = 4;

    /// <summary>Pipeline stages that may hold a full-size image at once (decode, upscale, post-process).</summary>
    private const long WorkingStages = 3;

    /// <summary>
    /// Estimates the size of the BC7 output: Σ target² × 1.33 bytes (1 byte per pixel plus about a third for mipmaps).
    /// </summary>
    /// <param name="targetMaxDimensions">The target maximum dimension of every texture to produce.</param>
    /// <returns>The estimated output size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="OverflowException">The total does not fit in 64 bits.</exception>
    public static long EstimateOutputBytes(IEnumerable<int> targetMaxDimensions)
    {
        ArgumentNullException.ThrowIfNull(targetMaxDimensions);

        long totalPixels = 0;
        foreach (var dimension in targetMaxDimensions)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(dimension, nameof(targetMaxDimensions));
            totalPixels = checked(totalPixels + ((long)dimension * dimension));
        }

        // × 1.33 in integer arithmetic, so the result is exact and does not depend on floating-point rounding.
        return checked(totalPixels * 133) / 100;
    }

    /// <summary>
    /// Estimates temporary space: concurrency × largest² × 4 bytes × 3 stages.
    /// </summary>
    /// <param name="concurrency">Number of textures in flight at once.</param>
    /// <param name="largestTargetDimension">The largest target dimension in the project.</param>
    /// <returns>The estimated temporary space in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is negative.</exception>
    public static long EstimateTempBytes(int concurrency, int largestTargetDimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(largestTargetDimension);

        return checked(concurrency * (long)largestTargetDimension * largestTargetDimension * BytesPerWorkingPixel * WorkingStages);
    }

    /// <summary>
    /// Ranks drives for the project folder: drives that fit first, then non-system drives, then the most free space.
    /// </summary>
    /// <param name="drives">The candidate drives.</param>
    /// <param name="requiredBytes">Space the project needs (output plus temporary).</param>
    /// <returns>All drives, best first. When none fits, the first entry has <see cref="DriveCandidate.Fits"/> set to <see langword="false"/>.</returns>
    public static IReadOnlyList<DriveCandidate> Rank(IEnumerable<(string Root, long Free, bool IsSystem)> drives, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        return drives
            .Select(drive => new DriveCandidate(drive.Root, drive.Free, drive.IsSystem, drive.Free >= requiredBytes))
            .OrderByDescending(candidate => candidate.Fits)
            .ThenBy(candidate => candidate.IsSystemDrive)
            .ThenByDescending(candidate => candidate.FreeBytes)
            .ThenBy(candidate => candidate.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lists the ready fixed drives of this machine with their free space.
    /// </summary>
    /// <returns>One entry per fixed, ready drive.</returns>
    public static IReadOnlyList<(string Root, long Free, bool IsSystem)> GetFixedDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        return DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => (drive.RootDirectory.FullName, drive.AvailableFreeSpace, string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
