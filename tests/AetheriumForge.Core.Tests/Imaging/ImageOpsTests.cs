using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging;

public sealed class ImageOpsTests
{
    private static readonly float[] PillowSourceRow = [0.0f, 0.1f, 0.9f, 0.3f, 0.5f, 0.7f, 0.2f, 1.0f];

    public static TheoryData<ResizeFilter, int, float[]> PillowReference => new()
    {
        // Generated with Pillow 'F' mode: Image.fromarray(row, 'F').resize((w, 1), filter); values outside 0–1 clamped.
        { ResizeFilter.Lanczos3, 3, [0.266635f, 0.531828f, 0.588388f] },
        { ResizeFilter.Lanczos3, 5, [0.010679f, 0.602066f, 0.470086f, 0.499948f, 0.661750f] },
        { ResizeFilter.Lanczos3, 13, [0.014997f, 0f, 0.128399f, 0.741604f, 0.832651f, 0.362998f, 0.279076f, 0.554869f, 0.750084f, 0.484832f, 0.190093f, 0.660757f, 1f] },
        { ResizeFilter.Bicubic, 3, [0.266041f, 0.527181f, 0.590726f] },
        { ResizeFilter.Bicubic, 5, [0.019840f, 0.607221f, 0.436792f, 0.523507f, 0.673320f] },
        { ResizeFilter.Bicubic, 13, [0f, 0f, 0.119319f, 0.699246f, 0.817916f, 0.341358f, 0.350000f, 0.527199f, 0.696475f, 0.527794f, 0.196615f, 0.673716f, 1f] },
    };

    [Fact]
    public void ToPlanarRgb_ThenToRgba_RoundTripsRgbAndOpaqueAlpha()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Noise(33, 17);
        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = 255;
        }

        // Act
        RgbaImage result = ImageOps.ToRgba(ImageOps.ToPlanarRgb(image), null);

        // Assert
        Assert.Equal(image.Pixels, result.Pixels);
    }

    [Fact]
    public void ToRgba_WithExtractedAlpha_RoundTripsAllChannels()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Noise(21, 11);

        // Act
        RgbaImage result = ImageOps.ToRgba(ImageOps.ToPlanarRgb(image), ImageOps.ExtractAlpha(image));

        // Assert
        Assert.Equal(image.Pixels, result.Pixels);
    }

    [Fact]
    public void ToPlanarRgb_Pixel_ProducesPlanarLayoutInUnitRange()
    {
        // Arrange
        var image = new RgbaImage(2, 1, [255, 0, 51, 7, 0, 102, 255, 9]);

        // Act
        PlanarImage planar = ImageOps.ToPlanarRgb(image);

        // Assert
        float[] expected = [1f, 0f, 0f, 0.4f, 0.2f, 1f];
        Assert.Equal(expected, planar.Data, new FloatComparer(1e-6f));
    }

    [Fact]
    public void ExtractAlpha_AllOpaque_ReturnsNull()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Gradient(16, 16, opaque: true);

        // Act
        PlanarImage? alpha = ImageOps.ExtractAlpha(image);

        // Assert
        Assert.Null(alpha);
    }

    [Fact]
    public void ExtractAlpha_OneTranslucentPixel_ReturnsAlphaPlane()
    {
        // Arrange
        RgbaImage image = ImagingTestData.Gradient(4, 4, opaque: true);
        image.Pixels[(5 * 4) + 3] = 254;

        // Act
        PlanarImage? alpha = ImageOps.ExtractAlpha(image);

        // Assert
        Assert.Equal((1, 254f / 255f, 1f), (alpha!.Channels, alpha.Data[5], alpha.Data[0]), new AlphaComparer());
    }

    [Fact]
    public void ToRgba_OutOfRangeAndNaN_ClampsAndRounds()
    {
        // Arrange
        var rgb = new PlanarImage(3, 4, 1, [-0.5f, 1.5f, float.NaN, 0.5f, 0.002f, 0.998f, 0.5f / 255f, 1.49f / 255f, 0f, 0f, 0f, 0f]);

        // Act
        RgbaImage result = ImageOps.ToRgba(rgb, null);

        // Assert: R values, then G, then B of the four pixels.
        byte[] p = result.Pixels;
        Assert.Equal(new byte[] { 0, 255, 0, 128, 1, 254, 1, 1, 0, 0, 0, 0 }, new[] { p[0], p[4], p[8], p[12], p[1], p[5], p[9], p[13], p[2], p[6], p[10], p[14] });
    }

    [Fact]
    public void ToRgba_AlphaSizeMismatch_ThrowsArgumentException()
    {
        // Arrange
        var rgb = new PlanarImage(3, 4, 4);
        var alpha = new PlanarImage(1, 4, 3);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => ImageOps.ToRgba(rgb, alpha));
    }

    [Theory]
    [InlineData(ResizeFilter.Lanczos3)]
    [InlineData(ResizeFilter.Bicubic)]
    public void Resize_SameSize_ReturnsExactCopy(ResizeFilter filter)
    {
        // Arrange
        PlanarImage source = ImageOps.ToPlanarRgb(ImagingTestData.Noise(19, 13));

        // Act
        PlanarImage result = ImageOps.Resize(source, 19, 13, filter);

        // Assert
        Assert.Equal(source.Data, result.Data);
        Assert.NotSame(source.Data, result.Data);
    }

    [Theory]
    [InlineData(ResizeFilter.Lanczos3, 64, 48, 17, 9)]
    [InlineData(ResizeFilter.Lanczos3, 16, 16, 61, 40)]
    [InlineData(ResizeFilter.Bicubic, 64, 48, 17, 9)]
    [InlineData(ResizeFilter.Bicubic, 16, 16, 61, 40)]
    public void Resize_ConstantImage_StaysConstant(ResizeFilter filter, int width, int height, int targetWidth, int targetHeight)
    {
        // Arrange
        PlanarImage source = ImagingTestData.Planar(3, width, height, (c, _, _) => 0.25f + (c * 0.3f));

        // Act
        PlanarImage result = ImageOps.Resize(source, targetWidth, targetHeight, filter);

        // Assert
        for (int c = 0; c < 3; c++)
        {
            float expected = 0.25f + (c * 0.3f);
            for (int i = 0; i < targetWidth * targetHeight; i++)
            {
                Assert.Equal(expected, result.Data[(c * targetWidth * targetHeight) + i], 1e-5f);
            }
        }
    }

    [Theory]
    [InlineData(64, 32, 16, 8)]
    [InlineData(16, 8, 64, 32)]
    [InlineData(17, 31, 5, 100)]
    [InlineData(1, 1, 3, 2)]
    public void Resize_TargetSize_ProducesRequestedDimensions(int width, int height, int targetWidth, int targetHeight)
    {
        // Arrange
        var source = new PlanarImage(2, width, height);

        // Act
        PlanarImage result = ImageOps.Resize(source, targetWidth, targetHeight, ResizeFilter.Lanczos3);

        // Assert
        Assert.Equal((2, targetWidth, targetHeight, 2 * targetWidth * targetHeight), (result.Channels, result.Width, result.Height, result.Data.Length));
    }

    [Theory]
    [MemberData(nameof(PillowReference))]
    public void Resize_Row_MatchesPillowFloatResample(ResizeFilter filter, int targetWidth, float[] expected)
    {
        // Arrange
        var source = new PlanarImage(1, PillowSourceRow.Length, 1, (float[])PillowSourceRow.Clone());

        // Act
        PlanarImage result = ImageOps.Resize(source, targetWidth, 1, filter);

        // Assert
        Assert.Equal(expected, result.Data, new FloatComparer(2e-5f));
    }

    [Theory]
    [MemberData(nameof(PillowReference))]
    public void Resize_Column_MatchesPillowFloatResample(ResizeFilter filter, int targetHeight, float[] expected)
    {
        // Arrange: the vertical pass must agree with the horizontal one.
        var source = new PlanarImage(1, 1, PillowSourceRow.Length, (float[])PillowSourceRow.Clone());

        // Act
        PlanarImage result = ImageOps.Resize(source, 1, targetHeight, filter);

        // Assert
        Assert.Equal(expected, result.Data, new FloatComparer(2e-5f));
    }

    [Fact]
    public void Resize_StepEdgeUpscale_LanczosRingsMoreThanBicubicAndBothStayInRange()
    {
        // Arrange: left half 0.2, right half 0.8.
        PlanarImage source = ImagingTestData.Planar(1, 16, 1, (_, x, _) => x < 8 ? 0.2f : 0.8f);

        // Act
        PlanarImage lanczos = ImageOps.Resize(source, 64, 1, ResizeFilter.Lanczos3);
        PlanarImage bicubic = ImageOps.Resize(source, 64, 1, ResizeFilter.Bicubic);

        // Assert: both are monotone across the far plateaus, Lanczos rings (overshoots) more than bicubic.
        float lanczosOvershoot = lanczos.Data.Max() - 0.8f;
        float bicubicOvershoot = bicubic.Data.Max() - 0.8f;
        Assert.All(lanczos.Data.Concat(bicubic.Data), v => Assert.InRange(v, 0f, 1f));
        Assert.Equal((0.2f, 0.8f), (lanczos.Data[0], lanczos.Data[63]), new TupleComparer(1e-5f));
        Assert.True(lanczosOvershoot > bicubicOvershoot && bicubicOvershoot > 0f, $"overshoot lanczos {lanczosOvershoot}, bicubic {bicubicOvershoot}");
    }

    [Fact]
    public void Resize_DownscaleByTwo_AveragesCheckerboardToGrey()
    {
        // Arrange: a 1-pixel checkerboard must not alias when downscaling (support widened by the factor).
        PlanarImage source = ImagingTestData.Planar(1, 64, 64, (_, x, y) => ((x + y) & 1) == 0 ? 1f : 0f);

        // Act
        PlanarImage result = ImageOps.Resize(source, 32, 32, ResizeFilter.Lanczos3);

        // Assert: interior samples are mid-grey.
        for (int y = 4; y < 28; y++)
        {
            for (int x = 4; x < 28; x++)
            {
                Assert.Equal(0.5f, result.Data[result.IndexOf(0, x, y)], 0.02f);
            }
        }
    }

    [Fact]
    public void Resize_BadDimensions_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var source = new PlanarImage(1, 4, 4);

        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOps.Resize(source, 0, 4, ResizeFilter.Bicubic));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.4)]
    [InlineData(0.5)]
    [InlineData(3.0)]
    public void RenormalizeNormals_AnyStrength_ProducesUnitVectors(double strength)
    {
        // Arrange: arbitrary colours, including invalid (non-unit, negative-z) normals.
        PlanarImage rgb = ImageOps.ToPlanarRgb(ImagingTestData.Noise(32, 32, seed: 99));

        // Act
        ImageOps.RenormalizeNormals(rgb, strength);

        // Assert
        int plane = rgb.Width * rgb.Height;
        for (int p = 0; p < plane; p++)
        {
            float x = (2 * rgb.Data[p]) - 1;
            float y = (2 * rgb.Data[plane + p]) - 1;
            float z = (2 * rgb.Data[(2 * plane) + p]) - 1;
            Assert.Equal(1f, MathF.Sqrt((x * x) + (y * y) + (z * z)), 1e-4f);
            Assert.True(z > 0f);
        }
    }

    [Fact]
    public void RenormalizeNormals_StrengthOneOnUnitNormals_IsNearIdentity()
    {
        // Arrange
        PlanarImage rgb = UnitNormals(24, 24);
        float[] before = (float[])rgb.Data.Clone();

        // Act
        ImageOps.RenormalizeNormals(rgb, 1.0);

        // Assert
        Assert.Equal(before, rgb.Data, new FloatComparer(1e-5f));
    }

    [Fact]
    public void RenormalizeNormals_StrengthAboveOne_IncreasesXyMagnitude()
    {
        // Arrange
        PlanarImage rgb = UnitNormals(24, 24);
        PlanarImage reference = UnitNormals(24, 24);

        // Act
        ImageOps.RenormalizeNormals(rgb, 1.5);

        // Assert
        int plane = rgb.Width * rgb.Height;
        for (int p = 0; p < plane; p++)
        {
            double before = XyLength(reference, plane, p);
            double after = XyLength(rgb, plane, p);
            if (before > 1e-3)
            {
                Assert.True(after > before, $"pixel {p}: {after} <= {before}");
            }
        }
    }

    [Fact]
    public void RenormalizeNormals_NegativeStrength_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var rgb = new PlanarImage(3, 2, 2);

        // Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOps.RenormalizeNormals(rgb, -1));
    }

    private static double XyLength(PlanarImage image, int plane, int p)
    {
        double x = (2.0 * image.Data[p]) - 1.0;
        double y = (2.0 * image.Data[plane + p]) - 1.0;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static PlanarImage UnitNormals(int width, int height)
    {
        var image = new PlanarImage(3, width, height);
        int plane = width * height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Tilt up to ~60° so z stays well above the 0.05 floor.
                double theta = (x / (double)(width - 1)) * (Math.PI / 3);
                double phi = (y / (double)(height - 1)) * (2 * Math.PI);
                int p = (y * width) + x;
                image.Data[p] = (float)((Math.Sin(theta) * Math.Cos(phi) + 1) / 2);
                image.Data[plane + p] = (float)((Math.Sin(theta) * Math.Sin(phi) + 1) / 2);
                image.Data[(2 * plane) + p] = (float)((Math.Cos(theta) + 1) / 2);
            }
        }

        return image;
    }

    private sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= tolerance;

        public int GetHashCode(float obj) => 0;
    }

    private sealed class TupleComparer(float tolerance) : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) x, (float, float) y) => MathF.Abs(x.Item1 - y.Item1) <= tolerance && MathF.Abs(x.Item2 - y.Item2) <= tolerance;

        public int GetHashCode((float, float) obj) => 0;
    }

    private sealed class AlphaComparer : IEqualityComparer<(int, float, float)>
    {
        public bool Equals((int, float, float) x, (int, float, float) y) =>
            x.Item1 == y.Item1 && MathF.Abs(x.Item2 - y.Item2) <= 1e-6f && MathF.Abs(x.Item3 - y.Item3) <= 1e-6f;

        public int GetHashCode((int, float, float) obj) => obj.Item1;
    }
}
