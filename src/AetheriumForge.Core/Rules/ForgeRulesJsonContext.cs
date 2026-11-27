using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Source-generated JSON metadata for the rules: camelCase names, enums as strings, indented output, nulls omitted,
/// comments and trailing commas accepted. <see cref="Compact"/> is the same without indentation (stored looks).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ForgeRulesDocument))]
[JsonSerializable(typeof(LookOverridesDocument))]
[JsonSerializable(typeof(TextureLooksDocument))]
internal sealed partial class ForgeRulesJsonContext : JsonSerializerContext
{
    private static ForgeRulesJsonContext? _compact;

    /// <summary>Gets a context with the same options but compact (not indented) output.</summary>
    public static ForgeRulesJsonContext Compact => _compact ??= new ForgeRulesJsonContext(new JsonSerializerOptions(Default.Options) { WriteIndented = false });
}
