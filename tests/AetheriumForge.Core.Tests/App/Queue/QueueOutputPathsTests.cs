using AetheriumForge.App.Pages.Queue;

namespace AetheriumForge.Core.Tests.App.Queue;

public sealed class QueueOutputPathsTests
{
    private static readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "af-output");

    [Theory]
    [InlineData(@"textures\clutter\bucket01.dds")]
    [InlineData("Textures/Clutter/Bucket01.DDS")]
    public void TryResolve_PathUnderTextures_ReturnsFileInOutputTextures(string jobPath)
    {
        // Act
        var accepted = QueueOutputPaths.TryResolve(_outputRoot, jobPath, out var file);

        // Assert
        Assert.Equal((true, Path.Combine(_outputRoot, "textures", "clutter", "bucket01.dds")), (accepted, file));
    }

    [Theory]
    [InlineData(@"textures\..\..\victim.dds")]
    [InlineData(@"textures\..\forge.db")]
    [InlineData(@"meshes\clutter\bucket01.nif")]
    [InlineData(@"textures")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\x.dds")]
    [InlineData(@"textures\.\x.dds")]
    [InlineData(@"textures\c:x.dds")]
    [InlineData("")]
    public void TryResolve_PathOutsideOutputTextures_Rejects(string jobPath)
    {
        // Act
        var accepted = QueueOutputPaths.TryResolve(_outputRoot, jobPath, out _);

        // Assert
        Assert.False(accepted);
    }
}
