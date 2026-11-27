using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Mutable JSON shape used only for reading rules. The source generator assigns every init-only property of a record
/// (overwriting initialisers with <see langword="default"/> when the JSON omits it), so reading goes through settable
/// properties that keep their defaults, then maps to the immutable records.
/// </summary>
internal sealed class ForgeRulesDocument
{
    public List<GroupRuleDocument?>? Groups { get; set; } = [];

    public int MinSource { get; set; } = ForgeRules.DefaultMinSource;

    public List<string>? Exclude { get; set; } = [.. ForgeRules.DefaultExcludedFolders];

    /// <summary>Maps to <see cref="ForgeRules"/> and validates the result.</summary>
    /// <exception cref="System.Text.Json.JsonException">The rules are invalid.</exception>
    public ForgeRules ToRules()
    {
        var rules = new ForgeRules
        {
            Groups = Groups?.Select(g => g?.ToRule()).ToArray()!,
            MinSource = MinSource,
            Exclude = Exclude?.ToArray()!,
        };
        rules.Validate();
        return rules;
    }
}

/// <summary>
/// Mutable JSON shape of one <see cref="GroupRule"/>; see <see cref="ForgeRulesDocument"/>.
/// </summary>
internal sealed class GroupRuleDocument
{
    public string? Name { get; set; }

    public List<string>? Match { get; set; } = ["*"];

    public int? MinSourceMax { get; set; }

    public int? MaxSourceMax { get; set; }

    public List<TextureKind>? Kinds { get; set; }

    public int Cap { get; set; } = 4096;

    public int MaxFactor { get; set; } = 4;

    public string? Model { get; set; }

    public double NormalStrength { get; set; } = 1.0;

    public GroupRule ToRule() => new()
    {
        Name = Name!,
        Match = Match?.ToArray()!,
        MinSourceMax = MinSourceMax,
        MaxSourceMax = MaxSourceMax,
        Kinds = Kinds?.ToArray(),
        Cap = Cap,
        MaxFactor = MaxFactor,
        Model = Model!,
        NormalStrength = NormalStrength,
    };
}
