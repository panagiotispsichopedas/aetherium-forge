using System.Diagnostics;
using AetheriumForge.Core.Projects;

namespace AetheriumForge.Core.Tests.Projects;

/// <summary>Containment through directory junctions (reparse points).</summary>
public sealed class PathGuardLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("af-pathguard-").FullName;

    public void Dispose()
    {
        // Remove the junction itself first, so the recursive delete never walks into its target.
        var link = Path.Combine(_root, "alias");
        if (Directory.Exists(link))
        {
            Directory.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string CreateJunction(string target)
    {
        var link = Path.Combine(_root, "alias");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.SkipUnless(process.ExitCode == 0 && Directory.Exists(link), "Directory junctions cannot be created here.");
        return link;
    }

    [Fact]
    public void IsUnder_ProjectThroughJunctionIntoGame_IsInside()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Skyrim Special Edition")).FullName;
        var alias = CreateJunction(game);

        var inside = PathGuard.IsUnder(Path.Combine(alias, "Data", "forge"), game);

        Assert.True(inside);
    }

    [Fact]
    public void IsUnder_GameReachedThroughJunction_ContainsRealProjectPath()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Skyrim Special Edition")).FullName;
        var alias = CreateJunction(game);

        var inside = PathGuard.IsUnder(Path.Combine(game, "forge"), alias);

        Assert.True(inside);
    }

    [Fact]
    public void ResolveFinalPath_MissingTail_KeepsTail()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        var alias = CreateJunction(game);

        var resolved = PathGuard.ResolveFinalPath(Path.Combine(alias, "not", "yet"));

        Assert.Equal(Path.Combine(game, "not", "yet"), resolved, ignoreCase: true);
    }

    [Fact]
    public void IsUnder_SiblingFolder_IsOutside()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "GameForge")).FullName;

        var inside = PathGuard.IsUnder(other, game);

        Assert.False(inside);
    }
}
