namespace AetheriumForge.Core.Models;

/// <summary>
/// Progress of one model download.
/// </summary>
/// <param name="BytesDone">Bytes on disk, including bytes resumed from an earlier attempt.</param>
/// <param name="BytesTotal">The expected file size.</param>
/// <param name="BytesPerSecond">The transfer rate of this attempt, excluding time spent paused.</param>
public sealed record DownloadProgress(long BytesDone, long BytesTotal, double BytesPerSecond)
{
    /// <summary>Gets the completed fraction, 0 to 1.</summary>
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

/// <summary>
/// A model installed by <see cref="ModelDownloader"/>.
/// </summary>
/// <param name="Name">The model name.</param>
/// <param name="Path">The full path of the <c>.onnx</c> file.</param>
/// <param name="SidecarPath">The full path of the sidecar JSON.</param>
/// <param name="LicencePath">The full path of the licence file, or <see langword="null"/> when the manifest names none.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Sha256">The verified SHA-256, lowercase hex.</param>
/// <param name="Resumed"><see langword="true"/> when an earlier partial download was reused.</param>
public sealed record InstalledModel(string Name, string Path, string SidecarPath, string? LicencePath, long Size, string Sha256, bool Resumed);

/// <summary>
/// The sidecar written next to a downloaded model. <see cref="Imaging.ModelCatalog"/> reads the first five fields and ignores the rest.
/// </summary>
/// <param name="DisplayName">The display name.</param>
/// <param name="Scale">The upscale factor.</param>
/// <param name="Precision">The precision, <c>fp32</c> or <c>fp16</c>.</param>
/// <param name="Licence">The SPDX licence identifier.</param>
/// <param name="RecommendedFor">The texture kinds the model suits.</param>
/// <param name="Sha256">The verified SHA-256 of the installed file, used to detect updates without re-hashing.</param>
/// <param name="Size">The installed file size.</param>
/// <param name="LastWriteTimeUtc">The installed file's last write time; a change means the recorded hash is stale.</param>
/// <param name="Tier">The recommended GPU tier.</param>
/// <param name="Family">The model family.</param>
internal sealed record InstalledModelSidecar(
    string? DisplayName,
    int? Scale,
    string? Precision,
    string? Licence,
    IReadOnlyList<string>? RecommendedFor,
    string? Sha256,
    long? Size,
    DateTime? LastWriteTimeUtc,
    string? Tier,
    string? Family);
