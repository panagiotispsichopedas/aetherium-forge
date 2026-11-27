using AetheriumForge.Cli.Hosting;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class ProjectLocatorTests
{
    private const string Game = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition";

    private static readonly (string Root, long Free, bool IsSystem)[] Drives =
    [
        (@"C:\", 500L << 30, true),
        (@"D:\", 900L << 30, false),
        (@"E:\", 100L << 30, false),
    ];

    private static bool None(string path) => false;

    [Fact]
    public void DefaultProjectFolder_SteamGame_UsesGameFolderName()
    {
        var folder = ProjectLocator.DefaultProjectFolder(@"D:\", Game);

        Assert.Equal(@"D:\AetheriumForge\Projects\Skyrim Special Edition", folder);
    }

    [Fact]
    public void ResolveForScan_NoProject_PicksBestNonSystemDrive()
    {
        var (layout, _) = ProjectLocator.ResolveForScan(null, Game, Drives, 50L << 30, None);

        Assert.Equal(@"D:\AetheriumForge\Projects\Skyrim Special Edition", layout.Root);
    }

    [Fact]
    public void ResolveForScan_LargeEstimate_PicksDriveThatFits()
    {
        var (layout, _) = ProjectLocator.ResolveForScan(null, Game, [(@"C:\", 500L << 30, true), (@"E:\", 100L << 30, false)], 200L << 30, None);

        Assert.Equal(@"C:\AetheriumForge\Projects\Skyrim Special Edition", layout.Root);
    }

    [Fact]
    public void ResolveForScan_ExistingProjectOnOtherDrive_ReusesIt()
    {
        var existing = @"E:\AetheriumForge\Projects\Skyrim Special Edition\forge.db";

        var (layout, reason) = ProjectLocator.ResolveForScan(null, Game, Drives, 0, p => string.Equals(p, existing, StringComparison.OrdinalIgnoreCase));

        Assert.Equal((@"E:\AetheriumForge\Projects\Skyrim Special Edition", "existing project"), (layout.Root, reason));
    }

    [Fact]
    public void ResolveForScan_ExplicitProject_WinsAndIsNormalised()
    {
        var (layout, _) = ProjectLocator.ResolveForScan(@"F:\Forge\MyProject\", Game, Drives, 0, _ => true);

        Assert.Equal(@"F:\Forge\MyProject", layout.Root);
    }

    [Fact]
    public void ResolveForScan_ProjectInsideGameFolder_Throws()
    {
        var ex = Assert.Throws<CliException>(() => ProjectLocator.ResolveForScan(Game + @"\Data\forge", Game, Drives, 0, None));

        Assert.Contains("inside the game folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExisting_ExplicitFolderWithoutDatabase_Throws()
    {
        var ex = Assert.Throws<CliException>(() => ProjectLocator.ResolveExisting(@"F:\Nothing", [], Drives, None));

        Assert.Contains("aetherium-forge scan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExisting_DiscoveredGame_FindsItsProject()
    {
        var existing = @"D:\AetheriumForge\Projects\Skyrim Special Edition\forge.db";

        var layout = ProjectLocator.ResolveExisting(null, [Game], Drives, p => string.Equals(p, existing, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(existing, layout.DatabasePath);
    }

    [Fact]
    public void ResolveExisting_NoProjectAnywhere_Throws()
    {
        Assert.Throws<CliException>(() => ProjectLocator.ResolveExisting(null, [Game], Drives, None));
    }

    [Fact]
    public void ProjectLayout_Folders_AreUnderRoot()
    {
        var layout = ProjectLayout.For(@"D:\P");

        Assert.Equal(
            (@"D:\P\forge.db", @"D:\P\work", @"D:\P\output", @"D:\P\logs\aetherium-forge-20261127.log"),
            (layout.DatabasePath, layout.WorkRoot, layout.OutputRoot, layout.DefaultLogFile(new DateOnly(2026, 11, 27))));
    }
}
