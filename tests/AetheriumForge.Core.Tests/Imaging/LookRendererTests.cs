using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Imaging;

public sealed class LookRendererTests
{
    /// <summary>Every look control away from neutral (normal strength and specular scale included).</summary>
    private static readonly LookSettings FullLook = new()
    {
        AiStrength = 0.6,
        Sharpen = new SharpenSettings(1.0, 1.5),
        Denoise = 0.5,
        Brightness = 0.05,
        Contrast = 1.3,
        Gamma = 1.2,
        Saturation = 1.4,
        Temperature = 0.5,
        NormalStrength = 1.7,
        SpecularScale = 1.5,
    };

    /// <summary>Only the controls that must never touch data kinds (sharpen and colour grading).</summary>
    private static readonly LookSettings ColorOnlyLook = new()
    {
        Sharpen = new SharpenSettings(2.0, 1.0),
        Brightness = 0.2,
        Contrast = 1.5,
        Gamma = 0.7,
        Saturation = 0.0,
        Temperature = -1.0,
    };

    public static TheoryData<TextureKind> AllKinds => [.. Enum.GetValues<TextureKind>()];

    public static TheoryData<TextureKind> DataKinds =>
    [
        TextureKind.Normal,
        TextureKind.ModelSpaceNormal,
        TextureKind.Specular,
        TextureKind.Environment,
        TextureKind.EnvironmentMask,
        TextureKind.Parallax,
        TextureKind.SkinTint,
    ];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void PostProcess_NeutralLook_IsByteIdenticalToM2Path(TextureKind kind)
    {
        // Arrange: a 4× model output (noise) resized down to an odd target, with alpha.
        PlanarImage ai = ImageOps.ToPlanarRgb(ImagingTestData.Noise(64, 48, seed: 7));
        PlanarImage alpha = ImageOps.ExtractAlpha(ImagingTestData.Gradient(16, 12))!;
        var job = new TextureJob(@"textures\t\a.dds", "a.bsa", kind, 41, 30, "m", 1.4, Look: LookSettings.Neutral with { NormalStrength = 1.4 });
        byte[] expected = M2PostProcess(job, ai, alpha).Pixels;

        // Act
        byte[] actual = TextureProcessor.PostProcess(job, ai, alpha, sourceRgb: ImageOps.ToPlanarRgb(ImagingTestData.Noise(16, 12))).Pixels;

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void PostProcess_NoLookOnJob_IsByteIdenticalToM2Path(TextureKind kind)
    {
        // Arrange
        PlanarImage ai = ImageOps.ToPlanarRgb(ImagingTestData.Noise(32, 32, seed: 3));
        var job = new TextureJob(@"textures\t\a.dds", "a.bsa", kind, 32, 32, "m", 0.8);
        byte[] expected = M2PostProcess(job, ai, null).Pixels;

        // Act
        byte[] actual = TextureProcessor.PostProcess(job, ai, null).Pixels;

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(DataKinds))]
    public void Render_SharpenAndGradingOnDataKind_ChangeNothing(TextureKind kind)
    {
        // Arrange
        var input = Input(kind, withAlpha: true);
        byte[] neutral = LookRenderer.Render(input, null).Pixels;

        // Act
        byte[] graded = LookRenderer.Render(input, ColorOnlyLook).Pixels;

        // Assert
        Assert.Equal(neutral, graded);
    }

    [Theory]
    [InlineData(TextureKind.Diffuse)]
    [InlineData(TextureKind.Glow)]
    [InlineData(TextureKind.Backlight)]
    public void Render_SharpenAndGradingOnColorKind_ChangeTheImage(TextureKind kind)
    {
        // Arrange
        var input = Input(kind, withAlpha: false);
        byte[] neutral = LookRenderer.Render(input, null).Pixels;

        // Act
        byte[] graded = LookRenderer.Render(input, ColorOnlyLook).Pixels;

        // Assert
        Assert.NotEqual(neutral, graded);
    }

    [Fact]
    public void Render_FullLook_DoesNotModifyInputs()
    {
        // Arrange
        var input = Input(TextureKind.Normal, withAlpha: true);
        float[][] before = [(float[])input.AiRgb.Data.Clone(), (float[])input.LanczosRgb.Data.Clone(), (float[])input.Alpha!.Data.Clone()];

        // Act
        LookRenderer.Render(input, FullLook);
        LookRenderer.Render(input with { Kind = TextureKind.Diffuse }, FullLook);

        // Assert
        float[][] after = [input.AiRgb.Data, input.LanczosRgb.Data, input.Alpha.Data];
        Assert.Equal(before, after);
    }

    [Fact]
    public void Render_AiStrengthZero_GivesTheLanczosImage()
    {
        // Arrange
        var input = Input(TextureKind.Diffuse, withAlpha: false);
        byte[] lanczos = ImageOps.ToRgba(input.LanczosRgb, null).Pixels;

        // Act
        byte[] rendered = LookRenderer.Render(input, LookSettings.Neutral with { AiStrength = 0.0 }).Pixels;

        // Assert
        Assert.Equal(lanczos, rendered);
    }

    [Fact]
    public void Render_AiStrengthHalf_GivesTheMidpoint()
    {
        // Arrange
        var ai = Constant(1.0f);
        var lanczos = Constant(0.0f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(ai, lanczos, null, TextureKind.Parallax), LookSettings.Neutral with { AiStrength = 0.5 });

        // Assert: 0.5 × 255 = 127.5 → 128.
        Assert.All(Rgb(rendered), v => Assert.Equal(128, v));
    }

    [Fact]
    public void Render_AiStrengthBelowOneWithoutModel_KeepsTheImage()
    {
        // Arrange: no model ran, so the AI and Lanczos inputs are the same instance.
        PlanarImage image = ImageOps.ToPlanarRgb(ImagingTestData.Noise(8, 8));
        byte[] neutral = ImageOps.ToRgba(image, null).Pixels;

        // Act
        byte[] rendered = LookRenderer.Render(new LookInput(image, image, null, TextureKind.Diffuse), LookSettings.Neutral with { AiStrength = 0.3 }).Pixels;

        // Assert
        Assert.Equal(neutral, rendered);
    }

    [Fact]
    public void Render_Sharpen_IncreasesEdgeContrast()
    {
        // Arrange: a vertical edge, 0.3 on the left and 0.7 on the right.
        var edge = ImagingTestData.Planar(3, 16, 4, (_, x, _) => x < 8 ? 0.3f : 0.7f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(edge, edge, null, TextureKind.Diffuse), LookSettings.Neutral with { Sharpen = new SharpenSettings(1.0, 1.0) });

        // Assert: the dark side of the edge gets darker and the bright side brighter.
        Assert.True(Red(rendered, 7, 1) < 77 && Red(rendered, 8, 1) > 179, $"{Red(rendered, 7, 1)} / {Red(rendered, 8, 1)}");
    }

    [Fact]
    public void Render_Sharpen_KeepsFlatAreas()
    {
        // Arrange
        var flat = Constant(0.4f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(flat, flat, null, TextureKind.Diffuse), LookSettings.Neutral with { Sharpen = new SharpenSettings(3.0, 2.0) });

        // Assert
        Assert.All(Rgb(rendered), v => Assert.Equal(102, v));
    }

    [Fact]
    public void Render_PositiveBrightness_Brightens()
    {
        // Arrange
        var grey = Constant(0.5f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(grey, grey, null, TextureKind.Diffuse), LookSettings.Neutral with { Brightness = 0.1 });

        // Assert
        Assert.All(Rgb(rendered), v => Assert.True(v > 128, $"{v}"));
    }

    [Fact]
    public void Render_Contrast_KeepsLinearMidGrey()
    {
        // Arrange: sRGB-encoded linear 0.18.
        var midGrey = Constant((float)ImageOps.LinearToSrgb(ImageOps.LinearMidGrey));
        byte[] neutral = ImageOps.ToRgba(midGrey, null).Pixels;

        // Act
        byte[] rendered = LookRenderer.Render(new LookInput(midGrey, midGrey, null, TextureKind.Diffuse), LookSettings.Neutral with { Contrast = 2.0 }).Pixels;

        // Assert
        Assert.Equal(neutral, rendered);
    }

    [Fact]
    public void Render_ContrastAboveOne_SpreadsDarkAndLight()
    {
        // Arrange
        var image = ImagingTestData.Planar(3, 2, 1, (_, x, _) => x == 0 ? 0.25f : 0.85f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(image, image, null, TextureKind.Diffuse), LookSettings.Neutral with { Contrast = 1.5 });

        // Assert
        Assert.True(Red(rendered, 0, 0) < 64 && Red(rendered, 1, 0) > 217, $"{Red(rendered, 0, 0)} / {Red(rendered, 1, 0)}");
    }

    [Fact]
    public void Render_GammaAboveOne_BrightensMidTones()
    {
        // Arrange
        var grey = Constant(0.5f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(grey, grey, null, TextureKind.Diffuse), LookSettings.Neutral with { Gamma = 2.0 });

        // Assert: 0.5^(1/2) = 0.7071 → 180.
        Assert.All(Rgb(rendered), v => Assert.InRange(v, (byte)179, (byte)181));
    }

    [Fact]
    public void Render_SaturationZero_GivesGrey()
    {
        // Arrange
        var colour = ImagingTestData.Planar(3, 4, 4, (c, _, _) => c switch { 0 => 0.9f, 1 => 0.4f, _ => 0.1f });

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(colour, colour, null, TextureKind.Diffuse), LookSettings.Neutral with { Saturation = 0.0 });

        // Assert
        Assert.True(rendered.Pixels[0] == rendered.Pixels[1] && rendered.Pixels[1] == rendered.Pixels[2], $"{rendered.Pixels[0]} {rendered.Pixels[1]} {rendered.Pixels[2]}");
    }

    [Fact]
    public void Render_WarmTemperature_RaisesRedAndLowersBlue()
    {
        // Arrange
        var grey = Constant(0.5f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(grey, grey, null, TextureKind.Diffuse), LookSettings.Neutral with { Temperature = 1.0 });

        // Assert
        Assert.True(rendered.Pixels[0] > 128 && rendered.Pixels[2] < 128, $"R {rendered.Pixels[0]} B {rendered.Pixels[2]}");
    }

    [Fact]
    public void Render_Temperature_KeepsLuminance()
    {
        // Arrange
        var grey = Constant(0.5f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(grey, grey, null, TextureKind.Diffuse), LookSettings.Neutral with { Temperature = -1.0 });

        // Assert
        double luma = (0.2126 * Linear(rendered.Pixels[0])) + (0.7152 * Linear(rendered.Pixels[1])) + (0.0722 * Linear(rendered.Pixels[2]));
        Assert.InRange(luma, ImageOps.SrgbToLinear(0.5) - 0.005, ImageOps.SrgbToLinear(0.5) + 0.005);
    }

    [Fact]
    public void Render_SpecularScaleOnSpecular_ScalesRgbAndClamps()
    {
        // Arrange
        var image = ImagingTestData.Planar(3, 2, 1, (_, x, _) => x == 0 ? 0.4f : 0.8f);

        // Act
        RgbaImage rendered = LookRenderer.Render(new LookInput(image, image, null, TextureKind.Specular), LookSettings.Neutral with { SpecularScale = 1.5 });

        // Assert: 0.6 → 153; 1.2 → clamped to 255.
        Assert.Equal(((byte)153, (byte)255), (Red(rendered, 0, 0), Red(rendered, 1, 0)));
    }

    [Fact]
    public void Render_SpecularScaleOnNormal_ScalesAlphaOnly()
    {
        // Arrange
        var input = Input(TextureKind.Normal, withAlpha: true);
        RgbaImage neutral = LookRenderer.Render(input, null);
        var expectedAlpha = new PlanarImage(1, input.Alpha!.Width, input.Alpha.Height, [.. input.Alpha.Data.Select(a => Math.Min(a * 0.5f, 1f))]);
        byte[] expected = ImageOps.ToRgba(ImagingTestData.Planar(3, input.Alpha.Width, input.Alpha.Height, (c, x, y) => neutral.Pixels[((y * input.Alpha.Width) + x) * 4 + c] / 255f), expectedAlpha).Pixels;

        // Act
        byte[] rendered = LookRenderer.Render(input, LookSettings.Neutral with { SpecularScale = 0.5 }).Pixels;

        // Assert
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_SpecularScaleOnDiffuse_ChangesNothing()
    {
        // Arrange
        var input = Input(TextureKind.Diffuse, withAlpha: true);
        byte[] neutral = LookRenderer.Render(input, null).Pixels;

        // Act
        byte[] rendered = LookRenderer.Render(input, LookSettings.Neutral with { SpecularScale = 2.0 }).Pixels;

        // Assert
        Assert.Equal(neutral, rendered);
    }

    [Fact]
    public void Render_NormalStrength_MatchesRenormalizeNormals()
    {
        // Arrange
        var input = Input(TextureKind.Normal, withAlpha: false);
        var expected = new PlanarImage(3, input.AiRgb.Width, input.AiRgb.Height, (float[])input.AiRgb.Data.Clone());
        ImageOps.RenormalizeNormals(expected, 2.5);

        // Act
        byte[] rendered = LookRenderer.Render(input, LookSettings.Neutral with { NormalStrength = 2.5 }).Pixels;

        // Assert
        Assert.Equal(ImageOps.ToRgba(expected, null).Pixels, rendered);
    }

    [Fact]
    public void Render_FullLookOn2048Diffuse_CompletesWithinBudget()
    {
        // Arrange: 2048² only, to keep the CPU load small; 4096² is about 4× this.
        var ai = ImagingTestData.Planar(3, 2048, 2048, (c, x, y) => ((x * 7) + (y * 3) + (c * 11)) % 256 / 255f);
        var lanczos = new PlanarImage(3, 2048, 2048, (float[])ai.Data.Clone());
        var input = new LookInput(ai, lanczos, null, TextureKind.Diffuse);
        LookRenderer.Render(new LookInput(Constant(0.5f), Constant(0.5f), null, TextureKind.Diffuse), FullLook);

        // Act
        var clock = System.Diagnostics.Stopwatch.StartNew();
        LookRenderer.Render(input, FullLook);
        clock.Stop();
        TestContext.Current.SendDiagnosticMessage($"Full look on 2048² diffuse: {clock.ElapsedMilliseconds} ms");

        // Assert: generous, so a busy machine does not fail the suite; the measured time is reported above.
        Assert.InRange(clock.ElapsedMilliseconds, 0, 5000);
    }

    /// <summary>The M2 post-process (resize, renormalise normal maps, merge alpha), kept verbatim as the byte-identity reference.</summary>
    private static RgbaImage M2PostProcess(TextureJob job, PlanarImage rgb, PlanarImage? alpha)
    {
        var resized = rgb.Width == job.TargetWidth && rgb.Height == job.TargetHeight
            ? new PlanarImage(rgb.Channels, rgb.Width, rgb.Height, (float[])rgb.Data.Clone())
            : ImageOps.Resize(rgb, job.TargetWidth, job.TargetHeight, ResizeFilter.Lanczos3);

        if (job.Kind == TextureKind.Normal)
        {
            ImageOps.RenormalizeNormals(resized, job.NormalStrength);
        }

        var resizedAlpha = alpha is null ? null : ImageOps.Resize(alpha, job.TargetWidth, job.TargetHeight, ResizeFilter.Bicubic);

        return ImageOps.ToRgba(resized, resizedAlpha);
    }

    private static LookInput Input(TextureKind kind, bool withAlpha)
    {
        PlanarImage ai = ImageOps.ToPlanarRgb(ImagingTestData.Noise(24, 20, seed: 11));
        PlanarImage lanczos = ImageOps.ToPlanarRgb(ImagingTestData.Gradient(24, 20));
        PlanarImage? alpha = withAlpha ? ImageOps.ExtractAlpha(ImagingTestData.Gradient(24, 20)) : null;
        return new LookInput(ai, lanczos, alpha, kind);
    }

    private static PlanarImage Constant(float value) => ImagingTestData.Planar(3, 8, 8, (_, _, _) => value);

    private static IEnumerable<byte> Rgb(RgbaImage image) => image.Pixels.Where((_, i) => i % 4 != 3);

    private static byte Red(RgbaImage image, int x, int y) => image.Pixels[((y * image.Width) + x) * 4];

    private static double Linear(byte value) => ImageOps.SrgbToLinear(value / 255.0);
}
