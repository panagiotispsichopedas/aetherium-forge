namespace AetheriumForge.Core.Jobs;

/// <summary>
/// The scan's missing or unreadable archives, sorted by how much they matter (see <see cref="ScanImporter.ReviewMissingArchivesAsync"/>).
/// </summary>
/// <param name="Warnings">Archives that are present but unreadable, or that provided jobs before: their textures are missing from the scan.</param>
/// <param name="NotInstalled">Archives the INI files list but that are not installed and never provided anything (normal for SE).</param>
public sealed record MissingArchiveReview(IReadOnlyList<string> Warnings, IReadOnlyList<string> NotInstalled)
{
    /// <summary>Gets the review of a scan without missing archives.</summary>
    public static MissingArchiveReview None { get; } = new([], []);

    /// <summary>Formats the information line for <see cref="NotInstalled"/>.</summary>
    /// <returns>The line, or <see langword="null"/> when there is nothing to say.</returns>
    public string? FormatNotInstalled() => NotInstalled.Count == 0
        ? null
        : $"{string.Join(", ", NotInstalled)} {(NotInstalled.Count == 1 ? "is" : "are")} listed in the INI files but not installed; that's normal for SE.";

    /// <summary>Formats the warning for <see cref="Warnings"/>.</summary>
    /// <returns>The warning, or <see langword="null"/> when there is nothing to warn about.</returns>
    public string? FormatWarning() => Warnings.Count == 0
        ? null
        : $"{Warnings.Count} archive(s) are missing or unreadable: {string.Join(", ", Warnings)}. "
            + "Their textures are not in this scan; existing jobs from them were left unchanged. Check the game files and rescan.";
}
