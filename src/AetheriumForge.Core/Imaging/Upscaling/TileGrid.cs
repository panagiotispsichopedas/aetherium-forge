namespace AetheriumForge.Core.Imaging;

/// <summary>
/// One tile span along an axis: the core range the tile owns and the wider input range read with overlap.
/// </summary>
/// <param name="CoreStart">First owned source pixel.</param>
/// <param name="CoreEnd">One past the last owned source pixel.</param>
/// <param name="InputStart">First source pixel fed to the model (<paramref name="CoreStart"/> minus the leading overlap).</param>
/// <param name="InputEnd">One past the last source pixel fed to the model (<paramref name="CoreEnd"/> plus the trailing overlap).</param>
internal readonly record struct TileSpan(int CoreStart, int CoreEnd, int InputStart, int InputEnd)
{
    /// <summary>Gets the owned length.</summary>
    public int CoreLength => CoreEnd - CoreStart;

    /// <summary>Gets the input length including overlap.</summary>
    public int InputLength => InputEnd - InputStart;

    /// <summary>Gets the overlap before the core (equals the blend half-width of the leading seam).</summary>
    public int LeadingOverlap => CoreStart - InputStart;

    /// <summary>Gets the overlap after the core (equals the blend half-width of the trailing seam).</summary>
    public int TrailingOverlap => InputEnd - CoreEnd;
}

/// <summary>
/// Tile layout maths for <see cref="TiledUpscaler"/>.
/// </summary>
internal static class TileGrid
{
    /// <summary>
    /// Splits an axis into near-equal cores of at most <paramref name="tileSize"/> pixels, each widened by up to
    /// <paramref name="overlap"/> pixels on inner edges. The overlap at a seam is limited to half of the smaller neighbouring
    /// core, so blend bands of one tile never meet and no pixel is covered by more than two tiles along an axis.
    /// </summary>
    /// <param name="length">Axis length in pixels; at least 1.</param>
    /// <param name="tileSize">Largest core length; at least 1.</param>
    /// <param name="overlap">Requested overlap; at least 0.</param>
    /// <returns>The spans in order; one span when <paramref name="length"/> fits in one tile.</returns>
    public static TileSpan[] Split(int length, int tileSize, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);

        var count = (length + tileSize - 1) / tileSize;
        var baseLength = length / count;
        var remainder = length % count;

        var cores = new (int Start, int End)[count];
        var position = 0;
        for (var i = 0; i < count; i++)
        {
            var coreLength = baseLength + (i < remainder ? 1 : 0);
            cores[i] = (position, position + coreLength);
            position += coreLength;
        }

        // seams[i] is the overlap at the seam between core i and core i + 1.
        var seams = new int[Math.Max(0, count - 1)];
        for (var i = 0; i < seams.Length; i++)
        {
            var left = cores[i].End - cores[i].Start;
            var right = cores[i + 1].End - cores[i + 1].Start;
            seams[i] = Math.Min(overlap, Math.Min(left, right) / 2);
        }

        var spans = new TileSpan[count];
        for (var i = 0; i < count; i++)
        {
            var leading = i > 0 ? seams[i - 1] : 0;
            var trailing = i < seams.Length ? seams[i] : 0;
            spans[i] = new TileSpan(cores[i].Start, cores[i].End, cores[i].Start - leading, cores[i].End + trailing);
        }

        return spans;
    }

    /// <summary>
    /// Computes the linear feather weights of one tile along an axis, in output (scaled) pixels over the tile input range.
    /// Across every seam the weights of the two neighbouring tiles sum to 1; outside the blend bands the owning tile has weight 1
    /// and its overlap has weight 0.
    /// </summary>
    /// <param name="span">The tile span.</param>
    /// <param name="scale">Model scale.</param>
    /// <returns>One weight per output pixel of <c>span.InputLength × scale</c>.</returns>
    public static float[] FeatherWeights(TileSpan span, int scale)
    {
        var weights = new float[span.InputLength * scale];
        var leadingBand = span.LeadingOverlap * scale;
        var trailingBand = span.TrailingOverlap * scale;
        var coreStart = span.LeadingOverlap * scale;
        var coreEnd = coreStart + (span.CoreLength * scale);

        for (var p = 0; p < weights.Length; p++)
        {
            float weight;
            if (leadingBand > 0 && p < coreStart + leadingBand)
            {
                // Rises from 0 to 1 across [coreStart - band, coreStart + band); the left neighbour has the complement.
                weight = Ramp(p - (coreStart - leadingBand), leadingBand);
            }
            else if (trailingBand > 0 && p >= coreEnd - trailingBand)
            {
                weight = 1f - Ramp(p - (coreEnd - trailingBand), trailingBand);
            }
            else
            {
                weight = p >= coreStart && p < coreEnd ? 1f : 0f;
            }

            weights[p] = weight;
        }

        return weights;
    }

    private static float Ramp(int offset, int band) => (offset + 0.5f) / (2f * band);
}
