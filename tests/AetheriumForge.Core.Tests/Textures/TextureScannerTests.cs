using AetheriumForge.Core.Game;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Tests.Archives;
using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Tests.Textures;

/// <summary>
/// Builds a synthetic game folder with base, Creation Club and mod archives plus loose files, then scans it.
/// </summary>
public sealed class TextureScannerTests : IClassFixture<TextureScannerTests.SyntheticGame>
{
    private readonly SyntheticGame _game;

    public TextureScannerTests(SyntheticGame game) => _game = game;

    [Fact]
    public void ScanAsync_SyntheticGame_ReturnsOnlyUncoveredBaseTextures()
    {
        // Arrange
        string[] expected =
        [
            @"textures\cc\new.dds",
            @"textures\clutter\a.dds",
            @"textures\clutter\cc.dds",
            @"textures\x\e_msn.dds",
        ];

        // Act
        var paths = _game.Result.Textures.Select(t => t.Path).ToArray();

        // Assert
        Assert.Equal(expected, paths);
    }

    [Fact]
    public void ScanAsync_CreationClubOverridesVanilla_SourceIsCreationClubArchive()
    {
        var texture = _game.Result.Textures.Single(t => t.Path == @"textures\clutter\cc.dds");

        Assert.Equal("ccTest.bsa", texture.SourceArchive);
    }

    [Fact]
    public void ScanAsync_ClutterTexture_QueuedInClutterGroupAtCap()
    {
        var decision = _game.Result.Textures.Single(t => t.Path == @"textures\clutter\a.dds").Decision;

        Assert.Equal(("Clutter", 2048, 1024, false), (decision.Group, decision.TargetWidth, decision.TargetHeight, decision.IsSkipped));
    }

    [Fact]
    public void ScanAsync_ModelSpaceNormal_SkippedByKind()
    {
        var decision = _game.Result.Textures.Single(t => t.Path == @"textures\x\e_msn.dds").Decision;

        Assert.Equal(SkipReason.SkippedKind, decision.Skip);
    }

    [Fact]
    public void ScanAsync_ReferencedArchiveAbsent_ReportedAsMissing()
    {
        Assert.Equal(["Missing.bsa"], _game.Result.MissingArchives);
    }

    [Fact]
    public void ScanAsync_LooseTextures_Counted()
    {
        Assert.Equal(1, _game.Result.LooseTextureCount);
    }

    [Fact]
    public async Task ScanAsync_NullProfile_Throws()
    {
        var scanner = new TextureScanner();

        await Assert.ThrowsAsync<ArgumentNullException>(() => scanner.ScanAsync(null!, ForgeRules.Default, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Shared synthetic game; scanned once per test class.
    /// </summary>
    public sealed class SyntheticGame : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"af-scan-{Guid.NewGuid():N}");

        public ScanResult Result { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            var data = Path.Combine(_root, "Data");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(_root, "Skyrim.ccc"), "ccTest.esl\r\n").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(data, "ccTest.esl"), string.Empty).ConfigureAwait(false);

            var dds512x256 = DdsTestData.Build(512, 256);
            var dds256 = DdsTestData.Build(256, 256);

            new TestBsaBuilder()
                .Add(@"textures\clutter\a.dds", dds512x256)
                .Add(@"textures\clutter\b.dds", dds256)
                .Add(@"textures\architecture\c.dds", dds256)
                .Add(@"textures\actors\character\d.dds", dds256)
                .Add(@"textures\x\e_msn.dds", dds256)
                .Add(@"textures\clutter\cc.dds", dds256)
                .Add(@"meshes\clutter\a.nif", [1, 2, 3])
                .WriteTo(data, "Skyrim - Textures0.bsa");

            new TestBsaBuilder()
                .Add(@"textures\clutter\cc.dds", dds256)
                .Add(@"textures\cc\new.dds", dds256)
                .WriteTo(data, "ccTest.bsa");

            new TestBsaBuilder()
                .Add(@"textures\clutter\b.dds", dds256)
                .WriteTo(data, "ModA.bsa");

            var loose = Path.Combine(data, "textures", "architecture");
            Directory.CreateDirectory(loose);
            await File.WriteAllBytesAsync(Path.Combine(loose, "C.DDS"), dds256).ConfigureAwait(false);

            var profile = new GameProfile(
                data,
                ["Skyrim.esm", "ccTest.esl", "ModA.esp"],
                ["Skyrim - Textures0.bsa"],
                ["ccTest.bsa", "ModA.bsa", "Missing.bsa"]);

            Result = await new TextureScanner().ScanAsync(profile, ForgeRules.Default).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
