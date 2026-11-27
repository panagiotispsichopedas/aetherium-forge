using AetheriumForge.Cli.Hosting;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class ToolLocatorTests
{
    private const string ExeDir = @"D:\Repos\Forge\src\AetheriumForge.Cli\bin\Debug\net10.0-windows\win-x64";
    private const string ExeTool = ExeDir + @"\tools\texconv.exe";
    private const string RepoTool = @"D:\Repos\Forge\tools\texconv.exe";
    private const string Solution = @"D:\Repos\Forge\AetheriumForge.slnx";

    private static Func<string, bool> Exists(params string[] files) =>
        path => files.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FindTexconv_ExplicitPathExists_UsesIt()
    {
        var path = ToolLocator.FindTexconv(@"E:\Tools\texconv.exe", ExeDir, Exists(@"E:\Tools\texconv.exe", ExeTool));

        Assert.Equal(@"E:\Tools\texconv.exe", path);
    }

    [Fact]
    public void FindTexconv_ExplicitPathMissing_ThrowsWithoutFallingBack()
    {
        var ex = Assert.Throws<CliException>(() => ToolLocator.FindTexconv(@"E:\Tools\texconv.exe", ExeDir, Exists(ExeTool)));

        Assert.Contains(@"E:\Tools\texconv.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTexconv_BothDefaultsExist_PrefersExeDirectory()
    {
        var path = ToolLocator.FindTexconv(null, ExeDir, Exists(ExeTool, RepoTool, Solution));

        Assert.Equal(ExeTool, path);
    }

    [Fact]
    public void FindTexconv_OnlyRepositoryTool_WalksUpToSolution()
    {
        var path = ToolLocator.FindTexconv(null, ExeDir, Exists(RepoTool, Solution));

        Assert.Equal(RepoTool, path);
    }

    [Fact]
    public void TexconvCandidates_NoExplicitPath_ListsExeDirThenRepository()
    {
        var candidates = ToolLocator.TexconvCandidates(null, ExeDir, Exists(Solution));

        Assert.Equal([ExeTool, RepoTool], candidates);
    }

    [Fact]
    public void FindTexconv_NothingFound_PointsToFetchScript()
    {
        var ex = Assert.Throws<CliException>(() => ToolLocator.FindTexconv(null, ExeDir, Exists(Solution)));

        Assert.Contains(@"tools\fetch-tools.ps1", ex.Message, StringComparison.Ordinal);
    }
}
