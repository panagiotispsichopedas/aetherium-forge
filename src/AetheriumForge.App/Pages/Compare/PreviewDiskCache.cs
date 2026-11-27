using System.Security.Cryptography;
using System.Text;
using AetheriumForge.Core.Imaging;

namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// An optional on-disk cache of preview samples in <c>&lt;project&gt;\work\preview\</c>, so a restart does not re-run the AI. Planes are
/// stored as half floats (enough for 8-bit output); files are written to a temporary name and then moved into place.
/// </summary>
public sealed class PreviewDiskCache
{
    private const uint Magic = 0x31504641; // "AFP1"

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewDiskCache"/> class.
    /// </summary>
    /// <param name="folder">The cache folder; created on first write.</param>
    public PreviewDiskCache(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
    }

    /// <summary>Gets the cache folder.</summary>
    public string Folder { get; }

    /// <summary>Gets the file that holds <paramref name="request"/>'s sample.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The file path.</returns>
    public string PathFor(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.CacheKey)));
        return Path.Combine(Folder, $"{hash[..32]}.afp");
    }

    /// <summary>Reads a cached sample.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sample, or <see langword="null"/> when there is no (valid) file.</returns>
    /// <exception cref="IOException">The file exists but could not be read.</exception>
    public async Task<PreviewSample?> TryReadAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        var path = PathFor(request);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(request, bytes);
    }

    /// <summary>Writes a sample (replacing any existing file).</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="cancellationToken">Cancels the write; no partial file is left in place.</param>
    /// <returns>A task that completes when the file is in place.</returns>
    public async Task WriteAsync(PreviewSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Directory.CreateDirectory(Folder);
        var path = PathFor(sample.Request);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Encode(sample);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static byte[] Encode(PreviewSample sample)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(sample.Request.CacheKey);
            writer.Write(sample.UsedModel);
            writer.Write(sample.Source.Width);
            writer.Write(sample.Source.Height);
            writer.Write(sample.Source.Pixels);
            WritePlanes(writer, sample.AiRgb);
            var sameLanczos = ReferenceEquals(sample.AiRgb, sample.LanczosRgb);
            writer.Write(sameLanczos);
            if (!sameLanczos)
            {
                WritePlanes(writer, sample.LanczosRgb);
            }

            writer.Write(sample.Alpha is not null);
            if (sample.Alpha is not null)
            {
                WritePlanes(writer, sample.Alpha);
            }
        }

        return stream.ToArray();
    }

    internal static PreviewSample? Decode(PreviewRequest request, byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        if (bytes.Length < 8 || reader.ReadUInt32() != Magic || reader.ReadString() != request.CacheKey)
        {
            return null;
        }

        var usedModel = reader.ReadBoolean();
        var sourceWidth = reader.ReadInt32();
        var sourceHeight = reader.ReadInt32();
        var source = new RgbaImage(sourceWidth, sourceHeight, reader.ReadBytes(checked(sourceWidth * sourceHeight * 4)));
        var ai = ReadPlanes(reader);
        var lanczos = reader.ReadBoolean() ? ai : ReadPlanes(reader);
        var alpha = reader.ReadBoolean() ? ReadPlanes(reader) : null;
        return new PreviewSample(request, source, ai, lanczos, alpha, usedModel);
    }

    private static void WritePlanes(BinaryWriter writer, PlanarImage image)
    {
        writer.Write(image.Channels);
        writer.Write(image.Width);
        writer.Write(image.Height);
        foreach (var value in image.Data)
        {
            writer.Write((Half)value);
        }
    }

    private static PlanarImage ReadPlanes(BinaryReader reader)
    {
        var channels = reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var data = new float[checked(channels * width * height)];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (float)reader.ReadHalf();
        }

        return new PlanarImage(channels, width, height, data);
    }
}
