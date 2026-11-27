namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Decodes game DDS textures to RGBA and encodes RGBA to BC7 DDS. <see cref="Texconv"/> is the production implementation.
/// </summary>
public interface ITextureCodec
{
    /// <summary>
    /// Decodes the top mip of <paramref name="ddsPath"/> to 8-bit RGBA without changing pixel values.
    /// </summary>
    /// <param name="ddsPath">Source DDS file.</param>
    /// <param name="workFolder">Folder for temporary files.</param>
    /// <param name="ct">Cancels the decode.</param>
    /// <returns>The decoded image.</returns>
    Task<RgbaImage> DecodeAsync(string ddsPath, string workFolder, CancellationToken ct = default);

    /// <summary>
    /// Encodes <paramref name="image"/> as BC7 with a full mip chain and moves the result to <paramref name="outputDdsPath"/>.
    /// </summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="outputDdsPath">Final output path; directories are created.</param>
    /// <param name="workFolder">Folder for temporary files.</param>
    /// <param name="srgb"><see langword="true"/> to tag the output as <c>BC7_UNORM_SRGB</c> (keeps an sRGB source's format).</param>
    /// <param name="ct">Cancels the encode.</param>
    /// <returns>A task that completes when the output file is in place.</returns>
    Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, bool srgb, CancellationToken ct = default);
}
