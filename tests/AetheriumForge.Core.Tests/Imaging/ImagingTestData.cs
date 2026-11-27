using System.Buffers.Binary;
using AetheriumForge.Core.Imaging;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Tests.Imaging;

/// <summary>Builders and helpers shared by the imaging tests.</summary>
internal static class ImagingTestData
{
    public const string TexconvVariable = "AETHERIUM_TEXCONV";

    /// <summary>Gets the texconv path from <c>AETHERIUM_TEXCONV</c>, or <see langword="null"/> when unset or missing.</summary>
    public static string? TexconvPath
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable(TexconvVariable);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    /// <summary>A deterministic image with smooth gradients and a varying alpha channel.</summary>
    public static RgbaImage Gradient(int width, int height, bool opaque = false)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                image.Pixels[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                image.Pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                image.Pixels[i + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
                image.Pixels[i + 3] = opaque ? (byte)255 : (byte)(255 - (x * 128 / Math.Max(1, width - 1)));
            }
        }

        return image;
    }

    /// <summary>A pseudo-random image (fixed seed).</summary>
    public static RgbaImage Noise(int width, int height, int seed = 1234)
    {
        var image = new RgbaImage(width, height);
        new Random(seed).NextBytes(image.Pixels);
        return image;
    }

    /// <summary>Builds a legacy (non-DX10) 32-bit DDS with the given channel masks and raw texels.</summary>
    public static byte[] LegacyDds(int width, int height, uint redMask, uint greenMask, uint blueMask, uint alphaMask, ReadOnlySpan<uint> texels)
    {
        byte[] data = new byte[128 + (texels.Length * 4)];
        Span<byte> span = data;
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x100F);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], width * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], alphaMask != 0 ? 0x41u : 0x40u);
        BinaryPrimitives.WriteInt32LittleEndian(span[88..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[92..], redMask);
        BinaryPrimitives.WriteUInt32LittleEndian(span[96..], greenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(span[100..], blueMask);
        BinaryPrimitives.WriteUInt32LittleEndian(span[104..], alphaMask);
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000);
        for (int i = 0; i < texels.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(128 + (i * 4))..], texels[i]);
        }

        return data;
    }

    /// <summary>Creates a planar image filled by <paramref name="value"/>(channel, x, y).</summary>
    public static PlanarImage Planar(int channels, int width, int height, Func<int, int, int, float> value)
    {
        var image = new PlanarImage(channels, width, height);
        for (int c = 0; c < channels; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    image.Data[image.IndexOf(c, x, y)] = value(c, x, y);
                }
            }
        }

        return image;
    }
}

/// <summary>A unique temporary folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aetherium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>Collects log entries for assertions.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
