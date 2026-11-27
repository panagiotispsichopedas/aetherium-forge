using AetheriumForge.Cli.Commands;
using AetheriumForge.Cli.Hosting;
using AetheriumForge.Cli.Pipeline;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Cli;

public sealed class JobMapperTests
{
    private static JobRecord Job(int width, int height, int target, string kind = "Diffuse", string? model = "realesrgan-x4plus") =>
        new(1, @"textures\clutter\bucket01.dds", "Skyrim - Textures1.bsa", width, height, "BC1_UNORM", kind, "default", target, model, JobState.Queued, 0, null, null, null);

    [Theory]
    [InlineData(512, 256, 2048, 2048, 1024)]
    [InlineData(256, 512, 2048, 1024, 2048)]
    [InlineData(300, 300, 1200, 1200, 1200)]
    public void FitToMax_AspectRatio_IsKept(int width, int height, int target, int expectedWidth, int expectedHeight)
    {
        var size = JobMapper.FitToMax(width, height, target);

        Assert.Equal((expectedWidth, expectedHeight), size);
    }

    [Fact]
    public void Map_RowMatchesRules_UsesRuleTargetAndKind()
    {
        var rules = ForgeRules.Default;
        var expected = new RuleEvaluator(rules).Evaluate(@"textures\clutter\bucket01.dds", new DdsHeader(512, 256, 1, string.Empty, null, false));
        var mapper = new JobMapper(rules);

        var job = mapper.Map(Job(512, 256, Math.Max(expected.TargetWidth, expected.TargetHeight)));

        Assert.Equal((expected.TargetWidth, expected.TargetHeight, TextureKind.Diffuse), (job.TargetWidth, job.TargetHeight, job.Kind));
    }

    [Fact]
    public void Map_ModelOverride_ReplacesJobModel()
    {
        var mapper = new JobMapper(ForgeRules.Default, "realesr-general-x4v3");

        var job = mapper.Map(Job(512, 512, 2048));

        Assert.Equal("realesr-general-x4v3", job.Model);
    }

    [Fact]
    public void Map_NoSourceSize_Throws()
    {
        var mapper = new JobMapper(ForgeRules.Default);

        Assert.Throws<InvalidOperationException>(() => mapper.Map(Job(0, 0, 2048)));
    }

    [Fact]
    public void RequiredModels_ParallaxJobs_NeedNoModel()
    {
        var mapper = new JobMapper(ForgeRules.Default);
        JobRecord[] jobs = [Job(512, 512, 2048), Job(512, 512, 2048, kind: "Parallax", model: "other"), Job(512, 512, 2048)];

        var needed = ForgeCommand.RequiredModels(jobs, mapper);

        Assert.Equal(["realesrgan-x4plus"], needed);
    }

    [Fact]
    public void EnsureModelsInstalled_MissingModel_ExplainsHowToDownload()
    {
        var installed = new[] { new ModelInfo("realesr-general-x4v3", @"D:\M\realesr-general-x4v3.onnx", "General", 4, "fp32", "BSD-3-Clause", []) };

        var ex = Assert.Throws<CliException>(() => ForgeCommand.EnsureModelsInstalled(["realesrgan-x4plus"], installed, @"D:\M"));

        Assert.Contains("gh release download models-v1", ex.Message, StringComparison.Ordinal);
    }

    private static ForgeRules StrongNormals() => new()
    {
        Groups = [new GroupRule { Name = "default", Model = "realesrgan-x4plus", NormalStrength = 1.4 }],
    };

    [Fact]
    public void Map_RulesChangedButGroupStillMatches_KeepsGroupNormalStrength()
    {
        var mapper = new JobMapper(StrongNormals());

        var job = mapper.Map(Job(512, 512, 999, kind: "Normal"));

        Assert.Equal((999, 999, 1.4), (job.TargetWidth, job.TargetHeight, job.NormalStrength));
    }

    [Fact]
    public void Map_RulesNoLongerMatchButGroupNameExists_UsesNamedGroupStrength()
    {
        var mapper = new JobMapper(StrongNormals());

        // 64 px is below the rules' minimum source, so evaluation skips it; the row's group name still finds the group.
        var job = mapper.Map(Job(64, 64, 256, kind: "Normal"));

        Assert.Equal(1.4, job.NormalStrength);
    }

    [Fact]
    public void Map_NoGroupMatches_UsesNeutralStrength()
    {
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "other", Match = [@"textures\nothing\*"], Model = "m", NormalStrength = 1.4 }] };
        var mapper = new JobMapper(rules);

        var job = mapper.Map(Job(512, 512, 2048, kind: "Normal"));

        Assert.Equal(1.0, job.NormalStrength);
    }
}
