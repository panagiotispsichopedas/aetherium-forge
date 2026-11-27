using System.Text.Json.Nodes;
using AetheriumForge.Core.Gpu;

namespace AetheriumForge.Core.Tests.Gpu;

public sealed class GpuProfileStoreTests : IDisposable
{
    private static readonly GpuAdapterInfo _adapter = SimulatedGpu.Adapter;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-gpu-profiles-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StorePath => Path.Combine(_folder, "nested", "gpu-profiles.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static GpuProfile Profile(GpuAdapterInfo adapter, string model = "realesrgan-x4plus", int tile = 512) =>
        new(adapter.Luid, adapter.Name, adapter.DriverVersion, adapter.DirectMLDeviceId, model, "fp32", tile, 9.25, GpuTier.Standard, "realesrgan-x4plus", ManualTimeProvider.Start)
        {
            VendorId = adapter.VendorId,
            DeviceId = adapter.DeviceId,
        };

    [Fact]
    public async Task FindAsync_AfterSave_RoundTripsProfile()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        var profile = Profile(_adapter);
        await store.SaveAsync(profile, Ct);

        // Act
        using var reopened = new GpuProfileStore(StorePath);
        var found = await reopened.FindAsync(_adapter, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Equal(profile, found);
    }

    [Fact]
    public async Task FindAsync_LuidAndDeviceIndexChanged_FindsProfileWithCurrentIds()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        var afterReboot = _adapter with { Luid = 0x9999, DirectMLDeviceId = 1 };

        // Act
        var found = await store.FindAsync(afterReboot, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Equal((0x9999L, 1), (found?.AdapterLuid, found?.DirectMLDeviceId));
    }

    [Fact]
    public async Task FindAsync_DriverChanged_ReturnsNull()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);

        // Act
        var found = await store.FindAsync(_adapter with { DriverVersion = "32.0.15.7000" }, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task FindAsync_OtherModel_ReturnsNull()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);

        // Act
        var found = await store.FindAsync(_adapter, "realesr-general-x4v3", Ct);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task FindAsync_OtherDevice_ReturnsNull()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);

        // Act
        var found = await store.FindAsync(_adapter with { DeviceId = 0x1B81 }, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task SaveAsync_NewDriver_ReplacesOldProfile()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        var updated = Profile(_adapter with { DriverVersion = "32.0.15.7000" }, tile: 640);

        // Act
        await store.SaveAsync(updated, Ct);

        // Assert
        Assert.Equal([updated], await store.ListAsync(Ct));
    }

    [Fact]
    public async Task SaveAsync_TwoModels_KeepsBoth()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        await store.SaveAsync(Profile(_adapter), Ct);
        await store.SaveAsync(Profile(_adapter, "realesr-general-x4v3"), Ct);

        // Assert
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task SaveAsync_Profile_WritesCamelCaseJsonWithoutTemporaryFiles()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        await store.SaveAsync(Profile(_adapter), Ct);

        // Assert
        var json = await File.ReadAllTextAsync(StorePath, Ct);
        var files = Directory.GetFiles(Path.GetDirectoryName(StorePath)!);
        Assert.Equal(
            (true, true, true, 1),
            (json.Contains("\"schemaVersion\": 1", StringComparison.Ordinal), json.Contains("\"secondsPerMegapixel\"", StringComparison.Ordinal), json.Contains("\"tier\": \"Standard\"", StringComparison.Ordinal), files.Length));
    }

    [Fact]
    public async Task RemoveAsync_ExistingProfile_RemovesItAndReturnsTrue()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        await store.SaveAsync(Profile(_adapter, "realesr-general-x4v3"), Ct);

        // Act
        var removed = await store.RemoveAsync(_adapter with { Luid = 42 }, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Equal((true, "realesr-general-x4v3"), (removed, (await store.ListAsync(Ct)).Single().Model));
    }

    [Fact]
    public async Task RemoveAsync_MissingProfile_ReturnsFalse()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        var removed = await store.RemoveAsync(_adapter, "realesrgan-x4plus", Ct);

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public async Task ListAsync_MissingFile_ReturnsEmpty()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        var profiles = await store.ListAsync(Ct);

        // Assert
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task ListAsync_CorruptFile_ReturnsEmpty()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, "{ not json", Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var profiles = await store.ListAsync(Ct);

        // Assert
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task ListAsync_UnknownSchemaVersion_ReturnsEmpty()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, """{ "schemaVersion": 99, "profiles": [] }""", Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var profiles = await store.ListAsync(Ct);

        // Assert
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task FindAsync_NullProfilesArray_ReturnsNull()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, """{ "schemaVersion": 1, "profiles": null }""", Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var found = await store.FindAsync(_adapter, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task ListAsync_NullAndInvalidEntries_KeepsOnlyValidProfile()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(StorePath, Ct))!;
        var profiles = document["profiles"]!.AsArray();
        var valid = profiles[0]!;
        profiles.Add(null);
        profiles.Add(WithValue(valid, "tileSize", 0));
        profiles.Add(WithValue(valid, "secondsPerMegapixel", -1.0));
        profiles.Add(WithValue(valid, "precision", "int8"));
        profiles.Add(WithValue(valid, "model", ""));
        await File.WriteAllTextAsync(StorePath, document.ToJsonString(), Ct);

        // Act
        var listed = await store.ListAsync(Ct);

        // Assert
        Assert.Equal([Profile(_adapter)], listed);
    }

    [Fact]
    public async Task SaveAsync_FileWithNullEntry_ReplacesItWithoutThrowing()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, """{ "schemaVersion": 1, "profiles": [ null ] }""", Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var saved = await store.SaveAsync(Profile(_adapter), Ct);

        // Assert
        Assert.Equal((true, 1), (saved, (await store.ListAsync(Ct)).Count));
    }

    [Fact]
    public async Task ListAsync_FileOpenByWriterSharingAccess_ReadsProfiles()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        using var other = new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        // Act
        var listed = await store.ListAsync(Ct);

        // Assert
        Assert.Single(listed);
    }

    [Fact]
    public async Task ListAsync_FileLockedExclusively_ReturnsEmpty()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        using var other = new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var listed = await store.ListAsync(Ct);

        // Assert
        Assert.Empty(listed);
    }

    [Fact]
    public async Task SaveAsync_TargetBrieflyLocked_RetriesMove()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);
        await store.SaveAsync(Profile(_adapter), Ct);
        var other = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(150, Ct);
                await other.DisposeAsync();
            },
            Ct);

        // Act
        var saved = await store.SaveAsync(Profile(_adapter, "realesr-general-x4v3"), Ct);
        await release;

        // Assert
        Assert.Equal((true, 2), (saved, (await store.ListAsync(Ct)).Count));
    }

    [Fact]
    public async Task SaveAsync_ManyWritersAcrossInstances_KeepsEveryProfile()
    {
        // Arrange
        using var first = new GpuProfileStore(StorePath);
        using var second = new GpuProfileStore(StorePath);
        var models = Enumerable.Range(0, 16).Select(index => $"model-{index}").ToList();

        // Act
        await Task.WhenAll(models.Select((model, index) => (index % 2 == 0 ? first : second).SaveAsync(Profile(_adapter, model), Ct)));

        // Assert
        Assert.Equal(models.Order(StringComparer.Ordinal), (await first.ListAsync(Ct)).Select(profile => profile.Model).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_NewerSchemaVersion_RefusesAndKeepsFile()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        const string Newer = """{ "schemaVersion": 2, "profiles": [], "future": true }""";
        await File.WriteAllTextAsync(StorePath, Newer, Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var saved = await store.SaveAsync(Profile(_adapter), Ct);

        // Assert
        Assert.Equal((false, Newer), (saved, await File.ReadAllTextAsync(StorePath, Ct)));
    }

    [Fact]
    public async Task RemoveAsync_NewerSchemaVersion_RefusesAndKeepsFile()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        const string Newer = """{ "schemaVersion": 2, "profiles": [] }""";
        await File.WriteAllTextAsync(StorePath, Newer, Ct);
        using var store = new GpuProfileStore(StorePath);

        // Act
        var removed = await store.RemoveAsync(_adapter, "realesrgan-x4plus", Ct);

        // Assert
        Assert.Equal((false, Newer), (removed, await File.ReadAllTextAsync(StorePath, Ct)));
    }

    [Fact]
    public async Task SaveAsync_ProvisionalProfile_IsNotPersisted()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        var saved = await store.SaveAsync(Profile(_adapter) with { IsProvisional = true }, Ct);

        // Assert
        Assert.Equal((false, false), (saved, File.Exists(StorePath)));
    }

    [Fact]
    public async Task SaveAsync_InvalidProfile_IsNotPersisted()
    {
        // Arrange
        using var store = new GpuProfileStore(StorePath);

        // Act
        var saved = await store.SaveAsync(Profile(_adapter) with { SecondsPerMegapixel = double.NaN }, Ct);

        // Assert
        Assert.Equal((false, false), (saved, File.Exists(StorePath)));
    }

    private static JsonNode WithValue(JsonNode source, string property, JsonNode? value)
    {
        var copy = source.DeepClone();
        copy[property] = value;
        return copy;
    }

    [Fact]
    public void Constructor_NoPath_UsesLocalAppData()
    {
        // Act
        using var store = new GpuProfileStore();

        // Assert
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "gpu-profiles.json"),
            store.FilePath);
    }

    [Fact]
    public void NeedsRecalibration_MissingProfile_ReturnsTrue()
    {
        // Act
        var needed = GpuProfileStore.NeedsRecalibration(null, _adapter);

        // Assert
        Assert.True(needed);
    }

    [Fact]
    public void NeedsRecalibration_SameAdapterNewLuid_ReturnsFalse()
    {
        // Act
        var needed = GpuProfileStore.NeedsRecalibration(Profile(_adapter), _adapter with { Luid = 77 });

        // Assert
        Assert.False(needed);
    }

    [Fact]
    public void NeedsRecalibration_DriverChanged_ReturnsTrue()
    {
        // Act
        var needed = GpuProfileStore.NeedsRecalibration(Profile(_adapter), _adapter with { DriverVersion = "32.0.15.7000" });

        // Assert
        Assert.True(needed);
    }

    [Fact]
    public void NeedsRecalibration_DifferentGpu_ReturnsTrue()
    {
        // Act
        var needed = GpuProfileStore.NeedsRecalibration(Profile(_adapter), _adapter with { Name = "NVIDIA GeForce RTX 4070", DeviceId = 0x2786 });

        // Assert
        Assert.True(needed);
    }
}
