namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// File helpers for rules import/export.
/// </summary>
internal static class RulesFileIo
{
    /// <summary>
    /// Writes <paramref name="text"/> to a temporary file beside <paramref name="path"/> and then moves it into place, so a failed write never
    /// leaves a truncated file. The temporary file is removed when the write fails.
    /// </summary>
    /// <param name="path">The destination file.</param>
    /// <param name="text">The text (UTF-8).</param>
    /// <returns>A task that completes when the file is in place.</returns>
    /// <exception cref="IOException">The file could not be written or moved.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file is not writable.</exception>
    public static async Task WriteAtomicAsync(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        string fullPath = Path.GetFullPath(path);
        string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, text).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }
}
