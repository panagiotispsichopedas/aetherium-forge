using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Textures;

public sealed class TextureClassifierTests
{
    [Theory]
    [InlineData(@"textures\clutter\bucket01.dds", TextureKind.Diffuse)]
    [InlineData(@"textures\clutter\bucket01_d.dds", TextureKind.Diffuse)]
    [InlineData(@"textures\clutter\bucket01_n.dds", TextureKind.Normal)]
    [InlineData(@"textures\clutter\bucket01_msn.dds", TextureKind.ModelSpaceNormal)]
    [InlineData(@"textures\clutter\bucket01_s.dds", TextureKind.Specular)]
    [InlineData(@"textures\clutter\bucket01_g.dds", TextureKind.Glow)]
    [InlineData(@"textures\clutter\bucket01_p.dds", TextureKind.Parallax)]
    [InlineData(@"textures\clutter\bucket01_e.dds", TextureKind.Environment)]
    [InlineData(@"textures\clutter\bucket01_em.dds", TextureKind.Environment)]
    [InlineData(@"textures\clutter\bucket01_m.dds", TextureKind.Environment)]
    [InlineData(@"textures\clutter\bucket01_envmask.dds", TextureKind.EnvironmentMask)]
    [InlineData(@"textures\clutter\bucket01_sk.dds", TextureKind.SkinTint)]
    [InlineData(@"textures\clutter\bucket01_b.dds", TextureKind.Backlight)]
    [InlineData(@"textures\clutter\bucket01_bl.dds", TextureKind.Backlight)]
    public void GetKind_KnownSuffix_ReturnsKind(string path, TextureKind expected)
    {
        // Act
        TextureKind kind = TextureClassifier.GetKind(path);

        // Assert
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(@"Textures\Armor\Iron\Cuirass_N.DDS", TextureKind.Normal)]
    [InlineData("textures/armor/iron/cuirass_msn.dds", TextureKind.ModelSpaceNormal)]
    [InlineData(@"textures\x\glass_EnvMask.dds", TextureKind.EnvironmentMask)]
    [InlineData(@"textures\x\body_s_n.dds", TextureKind.Normal)]
    [InlineData(@"textures\x\body_n_msn.dds", TextureKind.ModelSpaceNormal)]
    [InlineData(@"textures\x\leaf_m_bl.dds", TextureKind.Backlight)]
    [InlineData(@"textures\x\gem_n_em.dds", TextureKind.Environment)]
    [InlineData(@"textures\x\silkmask.dds", TextureKind.Diffuse)]
    [InlineData(@"textures\x\scene.dds", TextureKind.Diffuse)]
    [InlineData(@"textures\x\_n.dds", TextureKind.Diffuse)]
    [InlineData(@"textures\x_n\plain.dds", TextureKind.Diffuse)]
    [InlineData("cuirass_n", TextureKind.Normal)]
    public void GetKind_AmbiguousOrUnusualName_ReturnsExpectedKind(string path, TextureKind expected)
    {
        // Act
        TextureKind kind = TextureClassifier.GetKind(path);

        // Assert
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void GetKind_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TextureClassifier.GetKind(null!));
    }

    [Theory]
    [InlineData(TextureKind.Diffuse, false)]
    [InlineData(TextureKind.Normal, false)]
    [InlineData(TextureKind.ModelSpaceNormal, true)]
    [InlineData(TextureKind.Specular, false)]
    [InlineData(TextureKind.Glow, false)]
    [InlineData(TextureKind.Parallax, false)]
    [InlineData(TextureKind.Environment, true)]
    [InlineData(TextureKind.EnvironmentMask, false)]
    [InlineData(TextureKind.SkinTint, true)]
    [InlineData(TextureKind.Backlight, false)]
    public void IsSkippedByDefault_Kind_MatchesGuideTable(TextureKind kind, bool expected)
    {
        // Act
        bool skipped = TextureClassifier.IsSkippedByDefault(kind);

        // Assert
        Assert.Equal(expected, skipped);
    }
}
