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

    [Fact]
    public void TryResolve_InvalidPathCharacter_ReturnsFalseWithoutThrowing()
    {
        // Act
        var exception = Record.Exception(() => QueueOutputPaths.TryResolve(_outputRoot, $"textures/a{(char)0}b.dds", out _));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void IsFreeOfLinks_OrdinaryFolders_ReturnsTrue()
    {
        // Arrange
        var root = NewFolder();
        var file = Path.Combine(root, "textures", "clutter", "bucket01.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "dds");

        try
        {
            // Act
            var free = QueueOutputPaths.IsFreeOfLinks(root, file);

            // Assert
            Assert.True(free);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsFreeOfLinks_JunctionUnderTextures_ReturnsFalse()
    {
        // Arrange: <root>/textures/clutter is a junction to a folder outside the output.
        var root = NewFolder();
        var outside = NewFolder();
        File.WriteAllText(Path.Combine(outside, "bucket01.dds"), "victim");
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        var junction = Path.Combine(root, "textures", "clutter");
        CreateJunction(junction, outside);

        try
        {
            // Act
            var free = QueueOutputPaths.IsFreeOfLinks(root, Path.Combine(junction, "bucket01.dds"));

            // Assert
            Assert.False(free);
        }
        finally
        {
            Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return folder;
    }

    private static void CreateJunction(string link, string target)
    {
        // mklink /J needs no elevation or developer mode, unlike a symbolic link.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed");
    }
}
