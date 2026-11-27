using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Mutable JSON shape of the rules, used for reading and writing. The source generator assigns every init-only property of a record
/// (overwriting initialisers with <see langword="default"/> when the JSON omits it), so reading goes through settable properties that
/// keep their defaults, then maps to the immutable records. Writing goes through the same shape so that unset look controls and the
/// group-level <c>normalStrength</c> alias are written canonically (inside the group's <c>look</c>).
/// </summary>
internal sealed class ForgeRulesDocument
{
    public LookOverridesDocument? Look { get; set; }

    public List<GroupRuleDocument?>? Groups { get; set; } = [];

    public int MinSource { get; set; } = ForgeRules.DefaultMinSource;

    public List<string>? Exclude { get; set; } = [.. ForgeRules.DefaultExcludedFolders];

    /// <summary>Creates the document for writing <paramref name="rules"/>.</summary>
    public static ForgeRulesDocument From(ForgeRules rules) => new()
    {
        Look = rules.Look is null ? null : LookOverridesDocument.From(rules.Look),
        Groups = [.. rules.Groups.Select(GroupRuleDocument.From)],
        MinSource = rules.MinSource,
        Exclude = [.. rules.Exclude],
    };

    /// <summary>Maps to <see cref="ForgeRules"/> and validates the result.</summary>
    /// <exception cref="System.Text.Json.JsonException">The rules are invalid.</exception>
    public ForgeRules ToRules()
    {
        var rules = new ForgeRules
        {
            Look = Look?.ToOverrides(),
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

    /// <summary>Gets or sets the legacy group-level normal strength; <see langword="null"/> when omitted. Never written.</summary>
    public double? NormalStrength { get; set; }

    public LookOverridesDocument? Look { get; set; }

    /// <summary>Creates the document for writing <paramref name="rule"/>; the normal strength alias goes into the look.</summary>
    public static GroupRuleDocument? From(GroupRule? rule) => rule is null ? null : new()
    {
        Name = rule.Name,
        Match = rule.Match is null ? null : [.. rule.Match],
        MinSourceMax = rule.MinSourceMax,
        MaxSourceMax = rule.MaxSourceMax,
        Kinds = rule.Kinds is null ? null : [.. rule.Kinds],
        Cap = rule.Cap,
        MaxFactor = rule.MaxFactor,
        Model = rule.Model,
        Look = rule.ScopeLook is { } look ? LookOverridesDocument.From(look) : null,
    };

    /// <summary>
    /// Maps to <see cref="GroupRule"/>. A <c>look</c> without <c>normalStrength</c> takes the group-level <c>normalStrength</c> when that
    /// is present; when both are present the look's value wins. A group-level value without a <c>look</c> becomes a look that sets only
    /// the normal strength.
    /// </summary>
    public GroupRule ToRule()
    {
        LookOverrides? look = Look?.ToOverrides();
        if (NormalStrength is { } legacy && look?.NormalStrength is null)
        {
            look = (look ?? LookOverrides.Empty) with { NormalStrength = legacy };
        }

        return new GroupRule
        {
            Name = Name!,
            Match = Match?.ToArray()!,
            MinSourceMax = MinSourceMax,
            MaxSourceMax = MaxSourceMax,
            Kinds = Kinds?.ToArray(),
            Cap = Cap,
            MaxFactor = MaxFactor,
            Model = Model!,
            NormalStrength = look?.NormalStrength ?? 1.0,
            Look = look,
        };
    }
}

/// <summary>
/// Mutable JSON shape of <see cref="LookOverrides"/>; see <see cref="ForgeRulesDocument"/>. Omitted (or <see langword="null"/>) values are unset.
/// </summary>
internal sealed class LookOverridesDocument
{
    public double? AiStrength { get; set; }

    public SharpenOverridesDocument? Sharpen { get; set; }

    public double? Denoise { get; set; }

    public double? Brightness { get; set; }

    public double? Contrast { get; set; }

    public double? Gamma { get; set; }

    public double? Saturation { get; set; }

    public double? Temperature { get; set; }

    public double? NormalStrength { get; set; }

    public double? SpecularScale { get; set; }

    public static LookOverridesDocument From(LookOverrides look) => new()
    {
        AiStrength = look.AiStrength,
        Sharpen = look.SharpenAmount is null && look.SharpenRadius is null ? null : new SharpenOverridesDocument { Amount = look.SharpenAmount, Radius = look.SharpenRadius },
        Denoise = look.Denoise,
        Brightness = look.Brightness,
        Contrast = look.Contrast,
        Gamma = look.Gamma,
        Saturation = look.Saturation,
        Temperature = look.Temperature,
        NormalStrength = look.NormalStrength,
        SpecularScale = look.SpecularScale,
    };

    public LookOverrides ToOverrides() => new()
    {
        AiStrength = AiStrength,
        SharpenAmount = Sharpen?.Amount,
        SharpenRadius = Sharpen?.Radius,
        Denoise = Denoise,
        Brightness = Brightness,
        Contrast = Contrast,
        Gamma = Gamma,
        Saturation = Saturation,
        Temperature = Temperature,
        NormalStrength = NormalStrength,
        SpecularScale = SpecularScale,
    };
}

/// <summary>
/// Mutable JSON shape of the <c>sharpen</c> part of a look; see <see cref="ForgeRulesDocument"/>.
/// </summary>
internal sealed class SharpenOverridesDocument
{
    public double? Amount { get; set; }

    public double? Radius { get; set; }
}
