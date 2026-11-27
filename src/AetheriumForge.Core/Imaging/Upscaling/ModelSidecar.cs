using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// The optional <c>models\&lt;name&gt;.json</c> sidecar next to a model.
/// </summary>
internal sealed record ModelSidecar
{
    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the declared scale.</summary>
    public int? Scale { get; init; }

    /// <summary>Gets the precision (<c>fp32</c> or <c>fp16</c>).</summary>
    public string? Precision { get; init; }

    /// <summary>Gets the licence identifier.</summary>
    public string? Licence { get; init; }

    /// <summary>Gets the texture kinds the model suits.</summary>
    public IReadOnlyList<string>? RecommendedFor { get; init; }
}

/// <summary>
/// Source-generated JSON metadata for <see cref="ModelSidecar"/>: camelCase names, comments and trailing commas accepted.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ModelSidecar))]
internal sealed partial class ModelSidecarJsonContext : JsonSerializerContext;
