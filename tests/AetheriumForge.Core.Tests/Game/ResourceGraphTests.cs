using AetheriumForge.Core.Game;

namespace AetheriumForge.Core.Tests.Game;

public sealed class ResourceGraphTests
{
    private const string Textures0 = "Skyrim - Textures0.bsa";
    private const string Textures1 = "Skyrim - Textures1.bsa";

    private static ResourceGraph CreateGraph(IEnumerable<string>? loose = null, IEnumerable<string>? pluginArchive = null) =>
        new(
            loose ?? [],
            pluginArchive ?? [],
            new Dictionary<string, string>
            {
                [@"textures\clutter\bucket01.dds"] = Textures0,
                [@"Textures/Armor/Iron/Cuirass.DDS"] = Textures1,
                [@"textures\sky\clouds.dds"] = Textures0,
                [@"meshes\clutter\bucket01.nif"] = Textures0,
                [@"textures\effects\fx.png"] = Textures0,
                [@"interface\textures\icon.dds"] = Textures0,
            });

    [Fact]
    public void ComputeGapSet_NoOverrides_ReturnsAllTextureDdsSortedAndCanonical()
    {
        // Arrange
        ResourceGraph graph = CreateGraph();

        // Act
        IReadOnlyList<GapEntry> result = graph.ComputeGapSet();

        // Assert
        Assert.Equal(
            [
                new GapEntry(@"textures\armor\iron\cuirass.dds", Textures1),
                new GapEntry(@"textures\clutter\bucket01.dds", Textures0),
                new GapEntry(@"textures\sky\clouds.dds", Textures0),
            ],
            result);
    }

    [Fact]
    public void ComputeGapSet_LooseOverride_ExcludesPath()
    {
        // Arrange
        ResourceGraph graph = CreateGraph(loose: [@"TEXTURES/Clutter/Bucket01.dds"]);

        // Act
        IReadOnlyList<GapEntry> result = graph.ComputeGapSet();

        // Assert
        Assert.DoesNotContain(result, entry => entry.Path == @"textures\clutter\bucket01.dds");
    }

    [Fact]
    public void ComputeGapSet_PluginArchiveOverride_ExcludesPath()
    {
        // Arrange
        ResourceGraph graph = CreateGraph(pluginArchive: [@"textures\sky\CLOUDS.dds"]);

        // Act
        IReadOnlyList<GapEntry> result = graph.ComputeGapSet();

        // Assert
        Assert.DoesNotContain(result, entry => entry.Path == @"textures\sky\clouds.dds");
    }

    [Fact]
    public void ComputeGapSet_NonTexturesAndNonDds_AreIgnored()
    {
        // Arrange
        ResourceGraph graph = CreateGraph();

        // Act
        IReadOnlyList<GapEntry> result = graph.ComputeGapSet();

        // Assert
        Assert.All(result, entry => Assert.True(entry.Path.StartsWith(@"textures\", StringComparison.Ordinal) && entry.Path.EndsWith(".dds", StringComparison.Ordinal)));
    }

    [Fact]
    public void ComputeGapSet_ExclusionPredicate_VetoesCanonicalPath()
    {
        // Arrange
        ResourceGraph graph = CreateGraph();

        // Act
        IReadOnlyList<GapEntry> result = graph.ComputeGapSet(path => path.StartsWith(@"textures\sky\", StringComparison.Ordinal));

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetWinningIniSource_NotOverridden_ReturnsArchive()
    {
        // Arrange
        ResourceGraph graph = CreateGraph();

        // Act
        ResourceSource? result = graph.GetWinningIniSource(@"Textures\Clutter\Bucket01.dds");

        // Assert
        Assert.Equal(new ResourceSource(Textures0), result);
    }

    [Fact]
    public void GetWinningIniSource_LooseOverride_ReturnsNull()
    {
        // Arrange
        ResourceGraph graph = CreateGraph(loose: [@"textures\clutter\bucket01.dds"]);

        // Act
        ResourceSource? result = graph.GetWinningIniSource(@"textures\clutter\bucket01.dds");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void IsLoose_DifferentCaseAndSeparator_ReturnsTrue()
    {
        // Arrange
        ResourceGraph graph = CreateGraph(loose: [@"textures\clutter\bucket01.dds"]);

        // Act
        bool result = graph.IsLoose("TEXTURES/CLUTTER/BUCKET01.DDS");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Constructor_NullIniArchiveFiles_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ResourceGraph([], [], null!));
    }
}
