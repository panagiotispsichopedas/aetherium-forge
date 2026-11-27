using AetheriumForge.Cli;
using AetheriumForge.Cli.Commands;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class CommandParsingTests
{
    private readonly CliApp _app = new();

    [Fact]
    public void Parse_ForgeWithAllOptions_BindsValues()
    {
        var result = _app.Root.Parse(@"forge --project D:\P --models D:\M --model realesr-general-x4v3 --device 1 --tile 256 --limit 3 --texconv E:\t\texconv.exe --sequential");

        var forge = _app.Forge;
        Assert.Equal(
            (0, @"D:\P", @"D:\M", "realesr-general-x4v3", 1, 256, (int?)3, @"E:\t\texconv.exe", true),
            (result.Errors.Count, result.GetValue(forge.Project), result.GetValue(forge.Models), result.GetValue(forge.Model), result.GetValue(forge.Device),
             result.GetValue(forge.Tile), result.GetValue(forge.Limit), result.GetValue(forge.Texconv), result.GetValue(forge.Sequential)));
    }

    [Fact]
    public void Parse_ForgeWithoutOptions_UsesDefaults()
    {
        var result = _app.Root.Parse("forge");

        Assert.Equal((0, 0, ForgeCommand.DefaultTileSize, (int?)null, false), (result.Errors.Count, result.GetValue(_app.Forge.Device), result.GetValue(_app.Forge.Tile), result.GetValue(_app.Forge.Limit), result.GetValue(_app.Forge.Sequential)));
    }

    [Theory]
    [InlineData("forge --tile 10")]
    [InlineData("forge --tile 99999")]
    [InlineData("forge --limit 0")]
    [InlineData("forge --device -1")]
    [InlineData("forge --limit abc")]
    [InlineData("frobnicate")]
    public void Parse_InvalidInput_ReportsError(string commandLine)
    {
        var result = _app.Root.Parse(commandLine);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_Scan_BindsGameProjectAndRules()
    {
        var result = _app.Root.Parse(@"scan --game E:\Skyrim --project D:\P --rules r.json");

        Assert.Equal((0, @"E:\Skyrim", @"D:\P", "r.json"), (result.Errors.Count, result.GetValue(_app.Scan.Game), result.GetValue(_app.Scan.Project), result.GetValue(_app.Scan.Rules)));
    }

    [Fact]
    public void Parse_GlobalOptionsAfterSubcommand_AreAccepted()
    {
        var result = _app.Root.Parse(@"status --verbose --log D:\x.log");

        Assert.Equal((0, true, @"D:\x.log"), (result.Errors.Count, result.GetValue(_app.Globals.Verbose), result.GetValue(_app.Globals.Log)));
    }

    [Fact]
    public void Parse_Models_BindsFolder()
    {
        var result = _app.Root.Parse(@"models --models D:\M");

        Assert.Equal((0, @"D:\M"), (result.Errors.Count, result.GetValue(_app.Models.Models)));
    }
}
