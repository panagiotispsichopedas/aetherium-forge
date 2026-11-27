using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Pipeline;

public sealed class DecodedTextureTests
{
    [Fact]
    public void Dispose_WithSourceRgb_ReleasesSourceRgb()
    {
        // Arrange
        var job = new TextureJob(@"textures\t\a.dds", "a.bsa", TextureKind.Diffuse, 8, 8, "m", 1.0);
        var texture = new DecodedTexture(job, "out.dds", "work", _ => { }) { SourceRgb = new PlanarImage(3, 2, 2) };

        // Act
        texture.Dispose();

        // Assert
        Assert.Null(texture.SourceRgb);
    }
}
