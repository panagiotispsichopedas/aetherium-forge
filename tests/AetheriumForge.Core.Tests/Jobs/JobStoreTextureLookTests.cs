using System.Text.Json;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Rules;
using AetheriumForge.Core.Textures;
using Microsoft.Data.Sqlite;

namespace AetheriumForge.Core.Tests.Jobs;

public sealed class JobStoreTextureLookTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
    private readonly List<JobStore> _stores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_folder, "project.db");

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
    public async Task SetTextureLookAsync_ThenGet_ReturnsOverrideByNormalisedPath()
    {
        // Arrange
        JobStore store = await OpenAsync();
        var look = new LookOverrides { Saturation = 1.2, SharpenAmount = 0.4 };

        // Act
        await store.SetTextureLookAsync(@"Textures/Armor/Iron/Cuirass.dds", look, Ct);

        // Assert
        Assert.Equal(look, await store.GetTextureLookAsync(@"textures\armor\iron\cuirass.dds", Ct));
    }

    [Fact]
    public async Task SetTextureLookAsync_SamePathTwice_ReplacesOverride()
    {
        // Arrange
        JobStore store = await OpenAsync();
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);

        // Act
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Contrast = 1.3 }, Ct);

        // Assert
        Assert.Equal(new LookOverrides { Contrast = 1.3 }, (await store.ListTextureLooksAsync(Ct)).Single().Look);
    }

    [Fact]
    public async Task SetTextureLookAsync_EmptyOverride_RemovesRow()
    {
        // Arrange
        JobStore store = await OpenAsync();
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);

        // Act
        await store.SetTextureLookAsync(@"textures\a.dds", LookOverrides.Empty, Ct);

        // Assert
        Assert.Null(await store.GetTextureLookAsync(@"textures\a.dds", Ct));
    }

    [Fact]
    public async Task SetTextureLookAsync_OutOfRange_ThrowsArgumentException()
    {
        // Arrange
        JobStore store = await OpenAsync();

        // Act
        var act = () => store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 7 }, Ct);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(act);
    }

    [Fact]
    public async Task RemoveTextureLookAsync_Existing_RemovesAndReportsTrue()
    {
        // Arrange
        JobStore store = await OpenAsync();
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);

        // Act
        bool removed = await store.RemoveTextureLookAsync(@"TEXTURES\A.DDS", Ct);

        // Assert
        Assert.Equal((true, 0), (removed, (await store.ListTextureLooksAsync(Ct)).Count));
    }

    [Fact]
    public async Task RemoveTextureLookAsync_Missing_ReportsFalse()
    {
        // Arrange
        JobStore store = await OpenAsync();

        // Act
        bool removed = await store.RemoveTextureLookAsync(@"textures\none.dds", Ct);

        // Assert
        Assert.False(removed);
    }

    [Fact]
    public async Task ListTextureLooksAsync_SeveralOverrides_OrderedByPathWithTimestamp()
    {
        // Arrange
        JobStore store = await OpenAsync();
        await store.SetTextureLookAsync(@"textures\b.dds", new LookOverrides { Gamma = 1.1 }, Ct);
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);

        // Act
        IReadOnlyList<TextureLook> looks = await store.ListTextureLooksAsync(Ct);

        // Assert
        Assert.True(
            looks.Select(l => l.Path).SequenceEqual([@"textures\a.dds", @"textures\b.dds"]) && looks.All(l => l.UpdatedAt > DateTimeOffset.MinValue),
            string.Join(", ", looks));
    }

    [Fact]
    public async Task OpenAsync_DatabaseWithoutTextureLookTable_CreatesTable()
    {
        // Arrange: the M3 schema.
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync(Ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE project (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE job (
                  id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, archive TEXT NOT NULL,
                  src_w INT, src_h INT, format TEXT, kind TEXT, grp TEXT, target INT, model TEXT,
                  state TEXT NOT NULL, attempts INT DEFAULT 0, error TEXT, seconds REAL, finished_at TEXT);
                CREATE TABLE run (id INTEGER PRIMARY KEY, started TEXT, ended TEXT, done INT, failed INT);
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        SqliteConnection.ClearAllPools();

        // Act
        JobStore store = await OpenAsync();
        await store.SetTextureLookAsync(@"textures\a.dds", new LookOverrides { Gamma = 1.2 }, Ct);

        // Assert
        Assert.Single(await store.ListTextureLooksAsync(Ct));
    }

    [Fact]
    public async Task FindDoneAffectedByLookChangeAsync_GroupNormalStrengthEdit_ReturnsOnlyThatGroupsNormalMaps()
    {
        // Arrange: everything finished with the default rules.
        JobStore store = await OpenAsync();
        ForgeRules before = ForgeRules.Default;
        await FinishAsync(store, before, @"textures\armor\a.dds", "Armor", TextureKind.Diffuse);
        await FinishAsync(store, before, @"textures\armor\a_n.dds", "Armor", TextureKind.Normal);
        await FinishAsync(store, before, @"textures\weapons\w_n.dds", "Weapons", TextureKind.Normal);
        ForgeRules after = WithGroupLook(before, "Armor", new LookOverrides { NormalStrength = 2.0 });

        // Act
        IReadOnlyList<JobRecord> affected = await store.FindDoneAffectedByLookChangeAsync((job, texture) => LookResolver.Resolve(after, job.Group, texture), "Armor", Ct);

        // Assert
        Assert.Equal([@"textures\armor\a_n.dds"], affected.Select(j => j.Path));
    }

    [Fact]
    public async Task FindDoneAffectedByLookChangeAsync_GlobalEditShadowedByTextureOverride_SkipsThatTexture()
    {
        // Arrange: one texture sets its own saturation, so a global saturation edit does not reach it.
        JobStore store = await OpenAsync();
        ForgeRules before = ForgeRules.Default;
        var texture = new LookOverrides { Saturation = 0.8 };
        await store.SetTextureLookAsync(@"textures\armor\own.dds", texture, Ct);
        await FinishAsync(store, before, @"textures\armor\own.dds", "Armor", TextureKind.Diffuse, texture);
        await FinishAsync(store, before, @"textures\weapons\w.dds", "Weapons", TextureKind.Diffuse);
        ForgeRules after = before with { Look = new LookOverrides { Saturation = 1.4 } };

        // Act
        IReadOnlyList<JobRecord> affected = await store.FindDoneAffectedByLookChangeAsync((job, t) => LookResolver.Resolve(after, job.Group, t), null, Ct);

        // Assert
        Assert.Equal([@"textures\weapons\w.dds"], affected.Select(j => j.Path));
    }

    [Fact]
    public async Task FindDoneAffectedByLookChangeAsync_PendingTextureEdit_ReturnsThatTextureOnly()
    {
        // Arrange
        JobStore store = await OpenAsync();
        ForgeRules rules = ForgeRules.Default;
        await FinishAsync(store, rules, @"textures\armor\a.dds", "Armor", TextureKind.Diffuse);
        await FinishAsync(store, rules, @"textures\armor\b.dds", "Armor", TextureKind.Diffuse);
        var pending = new LookOverrides { Gamma = 1.3 };

        // Act: the UI substitutes the unsaved override for the edited path.
        IReadOnlyList<JobRecord> affected = await store.FindDoneAffectedByLookChangeAsync(
            (job, texture) => LookResolver.Resolve(rules, job.Group, job.Path == @"textures\armor\b.dds" ? pending : texture),
            "Armor",
            Ct);

        // Assert
        Assert.Equal([@"textures\armor\b.dds"], affected.Select(j => j.Path));
    }

    [Fact]
    public async Task FindDoneAffectedByLookChangeAsync_JobWithoutHash_IsAlwaysAffected()
    {
        // Arrange
        JobStore store = await OpenAsync();
        long id = await InsertAsync(store, @"textures\armor\legacy.dds", "Armor", TextureKind.Diffuse);
        await store.MarkDoneAsync(id, 1, Ct);

        // Act
        IReadOnlyList<JobRecord> affected = await store.FindDoneAffectedByLookChangeAsync((job, texture) => LookResolver.Resolve(ForgeRules.Default, job.Group, texture), null, Ct);

        // Assert
        Assert.Single(affected);
    }

    private static ForgeRules WithGroupLook(ForgeRules rules, string group, LookOverrides look) =>
        rules with { Groups = [.. rules.Groups.Select(g => g.Name == group ? g with { Look = look } : g)] };

    private static async Task<long> InsertAsync(JobStore store, string path, string group, TextureKind kind)
    {
        await store.UpsertJobsAsync([new JobRecord(0, path, "a.bsa", 512, 512, "BC1_UNORM", kind.ToString(), group, 2048, "m", JobState.Queued, 0, null, null, null)], Ct);
        await foreach (JobRecord job in store.GetJobsAsync(null, Ct))
        {
            if (job.Path == path)
            {
                return job.Id;
            }
        }

        throw new InvalidOperationException(path);
    }

    private static async Task FinishAsync(JobStore store, ForgeRules rules, string path, string group, TextureKind kind, LookOverrides? texture = null)
    {
        long id = await InsertAsync(store, path, group, kind);
        await store.MarkDoneAsync(id, 1, LookResolver.Resolve(rules, group, texture).GetStableHash(kind), Ct);
    }

    private async Task<JobStore> OpenAsync()
    {
        JobStore store = await JobStore.OpenAsync(DatabasePath, Ct);
        _stores.Add(store);
        return store;
    }
}
