using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Gpu;

/// <summary>
/// The root object of <c>gpu-profiles.json</c>.
/// </summary>
internal sealed class GpuProfileDocument
{
    /// <summary>Gets or sets the schema version (<see cref="GpuProfileStore.SchemaVersion"/>).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the profiles.</summary>
    public List<GpuProfile?>? Profiles { get; set; } = [];
}

/// <summary>
/// Source-generated JSON metadata for <see cref="GpuProfileDocument"/>: camelCase names, enums as strings, indented output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GpuProfileDocument))]
internal sealed partial class GpuProfileJsonContext : JsonSerializerContext;
