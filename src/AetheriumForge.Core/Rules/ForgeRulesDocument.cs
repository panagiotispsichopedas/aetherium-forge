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

    public LookSettingsDocument? Look { get; set; }

    /// <summary>
    /// Maps to <see cref="GroupRule"/>. A <c>look</c> block without <c>normalStrength</c> takes the group-level
    /// <c>normalStrength</c> (compatibility alias); when both are present the look's value wins.
    /// </summary>
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
        Look = Look?.ToLook(NormalStrength),
    };
}

/// <summary>
/// Mutable JSON shape of <see cref="LookSettings"/>; see <see cref="ForgeRulesDocument"/>. Omitted values keep their neutral defaults.
/// </summary>
internal sealed class LookSettingsDocument
{
    public double AiStrength { get; set; } = 1.0;

    public SharpenSettingsDocument? Sharpen { get; set; } = new();

    public double? Denoise { get; set; }

    public double Brightness { get; set; }

    public double Contrast { get; set; } = 1.0;

    public double Gamma { get; set; } = 1.0;

    public double Saturation { get; set; } = 1.0;

    public double Temperature { get; set; }

    /// <summary>Gets or sets the look's normal strength; <see langword="null"/> when omitted, so the group-level alias applies.</summary>
    public double? NormalStrength { get; set; }

    public double SpecularScale { get; set; } = 1.0;

    /// <summary>Maps to <see cref="LookSettings"/>, using <paramref name="groupNormalStrength"/> when the look has no normal strength.</summary>
    public LookSettings ToLook(double groupNormalStrength) => new()
    {
        AiStrength = AiStrength,
        Sharpen = Sharpen?.ToSettings()!,
        Denoise = Denoise,
        Brightness = Brightness,
        Contrast = Contrast,
        Gamma = Gamma,
        Saturation = Saturation,
        Temperature = Temperature,
        NormalStrength = NormalStrength ?? groupNormalStrength,
        SpecularScale = SpecularScale,
    };
}

/// <summary>
/// Mutable JSON shape of <see cref="SharpenSettings"/>; see <see cref="ForgeRulesDocument"/>.
/// </summary>
internal sealed class SharpenSettingsDocument
{
    public double Amount { get; set; }

    public double Radius { get; set; } = 1.0;

    public SharpenSettings ToSettings() => Amount == 0.0 && Radius == 1.0 ? SharpenSettings.None : new SharpenSettings(Amount, Radius);
}
