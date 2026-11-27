using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

/// <summary>
/// A pointwise fake model: nearest-neighbour upscale by <see cref="Scale"/> followed by a per-sample function.
/// Because every output pixel depends only on one input pixel, tiled and whole-image results must match exactly.
/// </summary>
internal sealed class FakeModelRunner : IModelRunner
{
    private readonly Func<float, float> _function;
    private readonly Lock _gate = new();
    private int _active;

    public FakeModelRunner(int scale = 4, Func<float, float>? function = null)
    {
        Scale = scale;
        _function = function ?? (value => value);
    }

    public int Scale { get; }

    public List<(int Width, int Height)> Calls { get; } = [];

    public int MaxConcurrency { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Called before each run with the 1-based call number and the tile.</summary>
    public Action<int, PlanarImage>? BeforeRun { get; init; }

    /// <summary>When it returns <see langword="true"/> for a tile, the run throws <see cref="ThrowFactory"/>.</summary>
    public Func<PlanarImage, bool>? FailWhen { get; init; }

    public Func<Exception> ThrowFactory { get; init; } = () => new VideoMemoryExhaustedException("simulated");

    public PlanarImage Run(PlanarImage tile)
    {
        int call;
        lock (_gate)
        {
            _active++;
            MaxConcurrency = Math.Max(MaxConcurrency, _active);
            Calls.Add((tile.Width, tile.Height));
            call = Calls.Count;
        }

        try
        {
            BeforeRun?.Invoke(call, tile);
            if (FailWhen?.Invoke(tile) == true)
            {
                throw ThrowFactory();
            }

            return Apply(tile, Scale, _function);
        }
        finally
        {
            lock (_gate)
            {
                _active--;
            }
        }
    }

    public void Dispose() => Disposed = true;

    /// <summary>The reference result: the model on the whole image, without tiling or clamping.</summary>
    public static PlanarImage Apply(PlanarImage source, int scale, Func<float, float> function)
    {
        var output = new PlanarImage(source.Channels, source.Width * scale, source.Height * scale);
        for (var channel = 0; channel < source.Channels; channel++)
        {
            for (var y = 0; y < output.Height; y++)
            {
                for (var x = 0; x < output.Width; x++)
                {
                    output.Data[output.IndexOf(channel, x, y)] = function(source.Data[source.IndexOf(channel, x / scale, y / scale)]);
                }
            }
        }

        return output;
    }

    /// <summary>A deterministic image with distinct values per pixel and channel.</summary>
    public static PlanarImage Pattern(int width, int height)
    {
        var image = new PlanarImage(3, width, height);
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image.Data[image.IndexOf(channel, x, y)] = ((x * 7) + (y * 13) + (channel * 29)) % 256 / 255f;
                }
            }
        }

        return image;
    }
}
