using System.Text.Json;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class TextureLooksFileTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly List<JobStore> _stores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (JobStore store in _stores)
        {
            await store.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ExportAsync_ThenImportIntoOtherProject_CopiesOverrides()
    {
        // Arrange
        JobStore source = await OpenAsync("a.db");
        JobStore target = await OpenAsync("b.db");
        await source.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);
        await source.SetTextureLookAsync(@"textures\b_n.dds", new LookOverrides { NormalStrength = 2.5, SpecularScale = 0.5 }, Ct);
        string file = Path.Combine(_folder, TextureLooksFile.FileName);

        // Act
        int exported = await TextureLooksFile.ExportAsync(source, file, Ct);
        int imported = await TextureLooksFile.ImportAsync(target, file, replaceExisting: false, Ct);

        // Assert
        var expected = (await source.ListTextureLooksAsync(Ct)).Select(l => (l.Path, l.Look));
        var actual = (await target.ListTextureLooksAsync(Ct)).Select(l => (l.Path, l.Look));
        Assert.True(exported == 2 && imported == 2 && expected.SequenceEqual(actual), $"{exported} / {imported}");
    }

    [Fact]
    public async Task ImportAsync_ReplaceExisting_RemovesOtherOverrides()
    {
        // Arrange
        JobStore store = await OpenAsync("a.db");
        await store.SetTextureLookAsync(@"textures\old.dds", new LookOverrides { Gamma = 1.2 }, Ct);
        string file = Path.Combine(_folder, TextureLooksFile.FileName);
        await File.WriteAllTextAsync(file, """{ "version": 1, "textures": [ { "path": "textures/New.dds", "look": { "contrast": 1.1 } } ] }""", Ct);

        // Act
        await TextureLooksFile.ImportAsync(store, file, replaceExisting: true, Ct);

        // Assert
        Assert.Equal([@"textures\new.dds"], (await store.ListTextureLooksAsync(Ct)).Select(l => l.Path));
    }

    [Fact]
    public async Task ImportAsync_InvalidEntry_ThrowsAndWritesNothing()
    {
        // Arrange
        JobStore store = await OpenAsync("a.db");
        await store.SetTextureLookAsync(@"textures\keep.dds", new LookOverrides { Gamma = 1.2 }, Ct);
        string file = Path.Combine(_folder, TextureLooksFile.FileName);
        await File.WriteAllTextAsync(
            file,
            """{ "version": 1, "textures": [ { "path": "textures\\ok.dds", "look": { "gamma": 1.1 } }, { "path": "textures\\bad.dds", "look": { "gamma": 9 } } ] }""",
            Ct);

        // Act
        var act = () => TextureLooksFile.ImportAsync(store, file, replaceExisting: true, Ct);

        // Assert
        await Assert.ThrowsAsync<JsonException>(act);
        Assert.Equal([@"textures\keep.dds"], (await store.ListTextureLooksAsync(Ct)).Select(l => l.Path));
    }

    [Theory]
    [InlineData("""{ "version": 2, "textures": [] }""")]
    [InlineData("""{ "version": 1, "textures": [ { "look": { "gamma": 1.1 } } ] }""")]
    [InlineData("""{ "version": 1, "textures": [ { "path": "textures\\a.dds" } ] }""")]
    public void FromJson_InvalidDocument_ThrowsJsonException(string json)
    {
        // Act
        var act = () => TextureLooksFile.FromJson(json);

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public void ToJson_Overrides_WritesVersionAndSortedPaths()
    {
        // Arrange
        TextureLook[] looks =
        [
            new(@"textures\b.dds", new LookOverrides { Gamma = 1.1 }, DateTimeOffset.UnixEpoch),
            new(@"textures\a.dds", new LookOverrides { Contrast = 1.2 }, DateTimeOffset.UnixEpoch),
        ];

        // Act
        using JsonDocument document = JsonDocument.Parse(TextureLooksFile.ToJson(looks));

        // Assert
        Assert.Equal(
            (1, @"textures\a.dds|textures\b.dds"),
            (document.RootElement.GetProperty("version").GetInt32(), string.Join("|", document.RootElement.GetProperty("textures").EnumerateArray().Select(e => e.GetProperty("path").GetString()))));
    }

    private async Task<JobStore> OpenAsync(string name)
    {
        JobStore store = await JobStore.OpenAsync(Path.Combine(_folder, name), Ct);
        _stores.Add(store);
        return store;
    }
}
