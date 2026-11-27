using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class LookPresetsTests
{
    public static TheoryData<string> PresetNames => [.. LookPresets.All.Select(preset => preset.Name)];

    private static IEnumerable<(LookControl Control, double Value)> Values(LookSettings look) =>
    [
        (LookControl.AiStrength, look.AiStrength),
        (LookControl.SharpenAmount, look.Sharpen.Amount),
        (LookControl.SharpenRadius, look.Sharpen.Radius),
        (LookControl.Brightness, look.Brightness),
        (LookControl.Contrast, look.Contrast),
        (LookControl.Gamma, look.Gamma),
        (LookControl.Saturation, look.Saturation),
        (LookControl.Temperature, look.Temperature),
        (LookControl.NormalStrength, look.NormalStrength),
        (LookControl.SpecularScale, look.SpecularScale),
    ];

    [Theory]
    [MemberData(nameof(PresetNames))]
    public void Preset_EveryValue_IsInsideTheControlRange(string name)
    {
        // Arrange
        var preset = LookPresets.All.Single(item => item.Name == name);

        // Act
        var outOfRange = Values(preset.Look).Where(value => !LookControls.RangeOf(value.Control).Contains(value.Value)).ToList();

        // Assert
        Assert.Empty(outOfRange);
    }

    [Fact]
    public void All_ListsTheFiveGuidePresetsInOrder()
    {
        // Arrange
        string[] expected = ["Neutral", "Crisp", "Soft", "Vivid", "Deep relief"];

        // Act
        var names = LookPresets.All.Select(preset => preset.Name);

        // Assert
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Neutral_IsTheNeutralLook()
    {
        // Arrange
        var look = LookPresets.Neutral.Look;

        // Act
        var neutral = look.IsNeutral;

        // Assert
        Assert.True(neutral);
    }

    [Fact]
    public void Ranges_NeutralValues_MatchLookSettingsDefaults()
    {
        // Arrange
        var defaults = Values(LookSettings.Neutral);

        // Act
        var mismatched = defaults.Where(value => LookControls.RangeOf(value.Control).Neutral != value.Value).ToList();

        // Assert
        Assert.Empty(mismatched);
    }
}
