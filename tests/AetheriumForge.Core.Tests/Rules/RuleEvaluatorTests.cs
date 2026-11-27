using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Rules;

public sealed class RuleEvaluatorTests
{
    private static readonly RuleEvaluator _defaultEvaluator = new(ForgeRules.Default);

    private static DdsHeader Header(int width, int height, bool cubemap = false) => new(width, height, 1, "DXT1", null, cubemap);

    [Fact]
    public void Evaluate_ClutterDiffuse_UsesClutterGroup()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\clutter\bucket01.dds", Header(512, 512));

        // Assert
        Assert.Equal(new TextureDecision(SkipReason.None, TextureKind.Diffuse, "Clutter", 2048, 2048, "realesrgan-x4plus", 1.4, LookSettings.Neutral with { NormalStrength = 1.4 }), decision);
    }

    [Fact]
    public void Evaluate_NonClutterPath_FallsThroughToLarge()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\armor\iron\cuirass_n.dds", Header(1024, 1024));

        // Assert
        Assert.Equal(new TextureDecision(SkipReason.None, TextureKind.Normal, "Large", 4096, 4096, "realesrgan-x4plus", 1.0, LookSettings.Neutral), decision);
    }

    [Fact]
    public void Evaluate_MixedCaseAndForwardSlashes_MatchesClutterGroup()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate("Textures/Clutter/Bucket01.DDS", Header(512, 512));

        // Assert
        Assert.Equal("Clutter", decision.Group);
    }

    [Fact]
    public void Evaluate_TwoMatchingGroups_FirstGroupWins()
    {
        // Arrange
        var rules = new ForgeRules
        {
            Groups =
            [
                new GroupRule { Name = "First", Match = [@"textures\armor\*"], Model = "a" },
                new GroupRule { Name = "Second", Match = [@"*\iron\*"], Model = "b" },
            ],
        };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\armor\iron\cuirass.dds", Header(512, 512));

        // Assert
        Assert.Equal("First", decision.Group);
    }

    [Fact]
    public void Evaluate_MultiplePatternsInGroup_AnyPatternMatches()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Match = [@"textures\weapons\*", @"textures\armor\*"], Model = "m" }] };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\armor\a.dds", Header(512, 512));

        // Assert
        Assert.Equal("G", decision.Group);
    }

    [Fact]
    public void Evaluate_GlobQuestionMarkAndRegexMetacharacters_MatchLiterally()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Match = [@"textures\a+b\file?.dds"], Model = "m" }] };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision hit = evaluator.Evaluate(@"textures\a+b\file1.dds", Header(512, 512));
        TextureDecision miss = evaluator.Evaluate(@"textures\aab\file1.dds", Header(512, 512));

        // Assert
        Assert.True(hit.Group == "G" && miss.Skip == SkipReason.NoMatchingGroup);
    }

    [Theory]
    [InlineData(256, "Small")]
    [InlineData(512, "Small")]
    [InlineData(513, "Big")]
    [InlineData(1024, "Big")]
    public void Evaluate_SizeRangeFilters_SelectGroupBySourceMax(int sourceMax, string expectedGroup)
    {
        // Arrange
        var rules = new ForgeRules
        {
            Groups =
            [
                new GroupRule { Name = "Small", MaxSourceMax = 512, Cap = 4096, Model = "m" },
                new GroupRule { Name = "Big", MinSourceMax = 513, Cap = 4096, Model = "m" },
            ],
        };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\x\a.dds", Header(sourceMax, sourceMax / 2));

        // Assert
        Assert.Equal(expectedGroup, decision.Group);
    }

    [Fact]
    public void Evaluate_SizeOutsideEveryRange_ReturnsNoMatchingGroup()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "Mid", MinSourceMax = 256, MaxSourceMax = 512, Model = "m" }] };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\x\a.dds", Header(1024, 1024));

        // Assert
        Assert.Equal(new TextureDecision(SkipReason.NoMatchingGroup, TextureKind.Diffuse, null, 0, 0, null, 0), decision);
    }

    [Fact]
    public void Evaluate_KindsFilter_SkipsGroupForOtherKinds()
    {
        // Arrange
        var rules = new ForgeRules
        {
            Groups =
            [
                new GroupRule { Name = "Normals", Kinds = [TextureKind.Normal], Model = "n" },
                new GroupRule { Name = "Rest", Model = "r" },
            ],
        };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision normal = evaluator.Evaluate(@"textures\x\a_n.dds", Header(512, 512));
        TextureDecision diffuse = evaluator.Evaluate(@"textures\x\a.dds", Header(512, 512));

        // Assert
        Assert.Equal(("Normals", "Rest"), (normal.Group, diffuse.Group));
    }

    [Fact]
    public void Evaluate_ExcludedFolder_SkipsBeforeOtherChecks()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\actors\character\male\body_msn.dds", Header(0, 0, cubemap: true));

        // Assert
        Assert.Equal(new TextureDecision(SkipReason.ExcludedFolder, TextureKind.ModelSpaceNormal, null, 0, 0, null, 0), decision);
    }

    [Theory]
    [InlineData(@"textures\armor\a_msn.dds")]
    [InlineData(@"textures\armor\a_e.dds")]
    [InlineData(@"textures\armor\a_em.dds")]
    [InlineData(@"textures\armor\a_m.dds")]
    [InlineData(@"textures\actors\dragon\a_sk.dds")]
    public void Evaluate_SkippedKind_ReturnsSkippedKind(string path)
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(path, Header(512, 512, cubemap: true));

        // Assert
        Assert.Equal(SkipReason.SkippedKind, decision.Skip);
    }

    [Fact]
    public void Evaluate_EnvMaskSuffix_IsUpscaled()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\armor\a_envmask.dds", Header(512, 512));

        // Assert
        Assert.Equal((SkipReason.None, TextureKind.EnvironmentMask), (decision.Skip, decision.Kind));
    }

    [Fact]
    public void Evaluate_Cubemap_ReturnsCubemap()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\sky\sky.dds", Header(512, 512, cubemap: true));

        // Assert
        Assert.Equal(SkipReason.Cubemap, decision.Skip);
    }

    [Fact]
    public void Evaluate_DefaultHeader_ReturnsInvalidHeader()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\armor\a.dds", default);

        // Assert
        Assert.Equal(SkipReason.InvalidHeader, decision.Skip);
    }

    [Fact]
    public void Evaluate_NoGroups_ReturnsNoMatchingGroup()
    {
        // Arrange
        var evaluator = new RuleEvaluator(new ForgeRules());

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\armor\a.dds", Header(512, 512));

        // Assert
        Assert.Equal(SkipReason.NoMatchingGroup, decision.Skip);
    }

    [Theory]
    [InlineData(127, SkipReason.TooSmall)]
    [InlineData(128, SkipReason.None)]
    public void Evaluate_MinSourceEdge_SkipsOnlyBelowMinimum(int sourceMax, SkipReason expected)
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\armor\a.dds", Header(sourceMax, sourceMax));

        // Assert
        Assert.Equal(expected, decision.Skip);
    }

    [Fact]
    public void Evaluate_TooSmall_ReportsMatchedGroup()
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\clutter\a.dds", Header(64, 64));

        // Assert
        Assert.Equal(new TextureDecision(SkipReason.TooSmall, TextureKind.Diffuse, "Clutter", 0, 0, null, 0), decision);
    }

    [Theory]
    [InlineData(2047, SkipReason.None)]
    [InlineData(2048, SkipReason.AlreadyAtCap)]
    [InlineData(4096, SkipReason.AlreadyAtCap)]
    public void Evaluate_CapEdge_SkipsAtOrAboveCap(int sourceMax, SkipReason expected)
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\clutter\a.dds", Header(sourceMax, sourceMax));

        // Assert
        Assert.Equal(expected, decision.Skip);
    }

    [Theory]
    [InlineData(128, 128, 512, 512)]
    [InlineData(512, 512, 2048, 2048)]
    [InlineData(1024, 1024, 4096, 4096)]
    [InlineData(2048, 2048, 4096, 4096)]
    [InlineData(1024, 512, 4096, 2048)]
    [InlineData(512, 1024, 2048, 4096)]
    [InlineData(3000, 1000, 4096, 1365)]
    [InlineData(2000, 3, 4096, 6)]
    [InlineData(3000, 1, 4096, 1)]
    [InlineData(1000, 333, 4000, 1332)]
    public void Evaluate_LargeGroup_TargetIsMinOfCapAndFactorKeepingAspect(int width, int height, int expectedWidth, int expectedHeight)
    {
        // Act
        TextureDecision decision = _defaultEvaluator.Evaluate(@"textures\architecture\wall.dds", Header(width, height));

        // Assert
        Assert.Equal((expectedWidth, expectedHeight), (decision.TargetWidth, decision.TargetHeight));
    }

    [Fact]
    public void Evaluate_MaxFactorTwo_LimitsTargetBelowCap()
    {
        // Arrange
        var rules = new ForgeRules { Groups = [new GroupRule { Name = "G", Cap = 4096, MaxFactor = 2, Model = "m" }] };
        var evaluator = new RuleEvaluator(rules);

        // Act
        TextureDecision decision = evaluator.Evaluate(@"textures\x\a.dds", Header(512, 256));

        // Assert
        Assert.Equal((1024, 512), (decision.TargetWidth, decision.TargetHeight));
    }

    [Fact]
    public void Evaluate_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _defaultEvaluator.Evaluate(null!, Header(512, 512)));
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RuleEvaluator(null!));
    }

    [Fact]
    public void IsSkipped_SkipNone_IsFalse()
    {
        // Arrange
        var decision = new TextureDecision(SkipReason.None, TextureKind.Diffuse, "G", 1, 1, "m", 1);

        // Act
        bool skipped = decision.IsSkipped;

        // Assert
        Assert.False(skipped);
    }
}
