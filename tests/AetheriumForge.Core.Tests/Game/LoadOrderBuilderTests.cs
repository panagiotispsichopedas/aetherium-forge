using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class LoadOrderBuilderTests
{
    private static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    [Fact]
    public void Build_NoInputs_ReturnsImplicitMastersInOrder()
    {
        // Act
        IReadOnlyList<string> result = LoadOrderBuilder.Build([], [], _ => true);

        // Assert
        Assert.Equal(Masters, result);
    }

    [Fact]
    public void Build_CccEntries_KeepsOnlyExistingAfterMastersBeforeActive()
    {
        // Arrange
        string[] ccc = ["ccA.esm", "ccMissing.esm", "ccB.esl"];
        string[] active = ["Mod.esp"];

        // Act
        IReadOnlyList<string> result = LoadOrderBuilder.Build(active, ccc, name => name != "ccMissing.esm");

        // Assert
        Assert.Equal([.. Masters, "ccA.esm", "ccB.esl", "Mod.esp"], result);
    }

    [Fact]
    public void Build_ActiveRepeatsMastersAndCc_DeduplicatesCaseInsensitivelyKeepingFirstPosition()
    {
        // Arrange
        string[] ccc = ["ccA.esm"];
        string[] active = ["skyrim.esm", "Mod.esp", "CCA.ESM", "UPDATE.ESM", "mod.esp", "Other.esp"];

        // Act
        IReadOnlyList<string> result = LoadOrderBuilder.Build(active, ccc, _ => true);

        // Assert
        Assert.Equal([.. Masters, "ccA.esm", "Mod.esp", "Other.esp"], result);
    }

    [Fact]
    public void Build_NullExistsPredicate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LoadOrderBuilder.Build([], [], null!));
    }
}
