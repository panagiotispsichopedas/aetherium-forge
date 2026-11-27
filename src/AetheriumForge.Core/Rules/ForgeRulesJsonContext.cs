using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Source-generated JSON metadata for <see cref="ForgeRules"/>: camelCase names, enums as strings, indented output,
/// comments and trailing commas accepted.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ForgeRules))]
[JsonSerializable(typeof(ForgeRulesDocument))]
internal sealed partial class ForgeRulesJsonContext : JsonSerializerContext;
