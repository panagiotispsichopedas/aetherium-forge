using System.Text.Json;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// A partial look for one scope (IMPLEMENTATION_GUIDE §8, "Scopes: All → Group → This texture"). Each control is either set at this
/// scope or <see langword="null"/>, which means "inherit from the parent scope". <see cref="Resolve"/> turns a chain of scopes into the
/// fully resolved <see cref="LookSettings"/> that <c>LookRenderer</c> renders.
/// </summary>
/// <remarks>
/// <see cref="Denoise"/> cannot express "explicitly the model's default" at a lower scope: <see langword="null"/> always inherits.
/// Ranges are those of <see cref="LookSettings"/>; values outside them are rejected when read from JSON.
/// </remarks>
public sealed record LookOverrides
{
    /// <summary>Gets an override that sets nothing (inherits every control).</summary>
    public static LookOverrides Empty { get; } = new();

    /// <summary>Gets the AI-strength blend, or <see langword="null"/> to inherit.</summary>
    public double? AiStrength { get; init; }

    /// <summary>Gets the unsharp-mask amount, or <see langword="null"/> to inherit.</summary>
    public double? SharpenAmount { get; init; }

    /// <summary>Gets the unsharp-mask radius, or <see langword="null"/> to inherit.</summary>
    public double? SharpenRadius { get; init; }

    /// <summary>Gets the denoise strength, or <see langword="null"/> to inherit.</summary>
    public double? Denoise { get; init; }

    /// <summary>Gets the brightness offset, or <see langword="null"/> to inherit.</summary>
    public double? Brightness { get; init; }

    /// <summary>Gets the contrast multiplier, or <see langword="null"/> to inherit.</summary>
    public double? Contrast { get; init; }

    /// <summary>Gets the gamma, or <see langword="null"/> to inherit.</summary>
    public double? Gamma { get; init; }

    /// <summary>Gets the saturation multiplier, or <see langword="null"/> to inherit.</summary>
    public double? Saturation { get; init; }

    /// <summary>Gets the colour temperature shift, or <see langword="null"/> to inherit.</summary>
    public double? Temperature { get; init; }

    /// <summary>Gets the normal-map strength, or <see langword="null"/> to inherit.</summary>
    public double? NormalStrength { get; init; }

    /// <summary>Gets the specular scale, or <see langword="null"/> to inherit.</summary>
    public double? SpecularScale { get; init; }

    /// <summary>Gets a value indicating whether no control is set.</summary>
    public bool IsEmpty => this == Empty;

    /// <summary>
    /// Creates an override that sets every control to the value in <paramref name="settings"/> (a fully specified look).
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The override.</returns>
    public static LookOverrides From(LookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new LookOverrides
        {
            AiStrength = settings.AiStrength,
            SharpenAmount = settings.Sharpen.Amount,
            SharpenRadius = settings.Sharpen.Radius,
            Denoise = settings.Denoise,
            Brightness = settings.Brightness,
            Contrast = settings.Contrast,
            Gamma = settings.Gamma,
            Saturation = settings.Saturation,
            Temperature = settings.Temperature,
            NormalStrength = settings.NormalStrength,
            SpecularScale = settings.SpecularScale,
        };
    }

    /// <summary>
    /// Resolves this override over <paramref name="parent"/>: each control set here wins, every other control comes from the parent.
    /// </summary>
    /// <param name="parent">The resolved parent look; <see langword="null"/> means <see cref="LookSettings.Neutral"/>.</param>
    /// <returns>The resolved look.</returns>
    public LookSettings Resolve(LookSettings? parent)
    {
        parent ??= LookSettings.Neutral;
        double amount = SharpenAmount ?? parent.Sharpen.Amount;
        double radius = SharpenRadius ?? parent.Sharpen.Radius;
        return new LookSettings
        {
            AiStrength = AiStrength ?? parent.AiStrength,
            Sharpen = amount == SharpenSettings.None.Amount && radius == SharpenSettings.None.Radius ? SharpenSettings.None : new SharpenSettings(amount, radius),
            Denoise = Denoise ?? parent.Denoise,
            Brightness = Brightness ?? parent.Brightness,
            Contrast = Contrast ?? parent.Contrast,
            Gamma = Gamma ?? parent.Gamma,
            Saturation = Saturation ?? parent.Saturation,
            Temperature = Temperature ?? parent.Temperature,
            NormalStrength = NormalStrength ?? parent.NormalStrength,
            SpecularScale = SpecularScale ?? parent.SpecularScale,
        };
    }

    /// <summary>
    /// Layers this override over <paramref name="parent"/> without resolving: each control set here wins, unset controls come from the
    /// parent (and stay unset when the parent does not set them either).
    /// </summary>
    /// <param name="parent">The parent override, or <see langword="null"/>.</param>
    /// <returns>The combined override.</returns>
    public LookOverrides Over(LookOverrides? parent)
    {
        if (parent is null)
        {
            return this;
        }

        return new LookOverrides
        {
            AiStrength = AiStrength ?? parent.AiStrength,
            SharpenAmount = SharpenAmount ?? parent.SharpenAmount,
            SharpenRadius = SharpenRadius ?? parent.SharpenRadius,
            Denoise = Denoise ?? parent.Denoise,
            Brightness = Brightness ?? parent.Brightness,
            Contrast = Contrast ?? parent.Contrast,
            Gamma = Gamma ?? parent.Gamma,
            Saturation = Saturation ?? parent.Saturation,
            Temperature = Temperature ?? parent.Temperature,
            NormalStrength = NormalStrength ?? parent.NormalStrength,
            SpecularScale = SpecularScale ?? parent.SpecularScale,
        };
    }

    /// <summary>
    /// Serialises the override as compact camelCase JSON in the rules' <c>look</c> shape; unset controls are omitted.
    /// </summary>
    /// <returns>The JSON text, for example <c>{"saturation":1.2}</c>.</returns>
    public string ToJson() => JsonSerializer.Serialize(LookOverridesDocument.From(this), ForgeRulesJsonContext.Compact.LookOverridesDocument);

    /// <summary>
    /// Reads and validates an override from JSON in the rules' <c>look</c> shape. Comments and trailing commas are allowed.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The override.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is malformed or a value is out of range.</exception>
    public static LookOverrides FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        LookOverridesDocument document = JsonSerializer.Deserialize(json, ForgeRulesJsonContext.Default.LookOverridesDocument)
            ?? throw new JsonException("The look JSON is null.");
        LookOverrides look = document.ToOverrides();
        look.Validate("Look");
        return look;
    }

    /// <summary>
    /// Throws <see cref="JsonException"/> when a set value is outside its range or not finite.
    /// </summary>
    /// <param name="label">The owner, for example <c>Group 'Clutter'</c>, used in the message.</param>
    /// <exception cref="JsonException">A value is out of range.</exception>
    internal void Validate(string label)
    {
        Check(label, "aiStrength", AiStrength, LookSettings.MinAiStrength, LookSettings.MaxAiStrength);
        Check(label, "sharpen.amount", SharpenAmount, SharpenSettings.MinAmount, SharpenSettings.MaxAmount);
        Check(label, "sharpen.radius", SharpenRadius, SharpenSettings.MinRadius, SharpenSettings.MaxRadius);
        Check(label, "denoise", Denoise, LookSettings.MinDenoise, LookSettings.MaxDenoise);
        Check(label, "brightness", Brightness, LookSettings.MinBrightness, LookSettings.MaxBrightness);
        Check(label, "contrast", Contrast, LookSettings.MinContrast, LookSettings.MaxContrast);
        Check(label, "gamma", Gamma, LookSettings.MinGamma, LookSettings.MaxGamma);
        Check(label, "saturation", Saturation, LookSettings.MinSaturation, LookSettings.MaxSaturation);
        Check(label, "temperature", Temperature, LookSettings.MinTemperature, LookSettings.MaxTemperature);
        Check(label, "normalStrength", NormalStrength, LookSettings.MinNormalStrength, LookSettings.MaxNormalStrength);
        Check(label, "specularScale", SpecularScale, LookSettings.MinSpecularScale, LookSettings.MaxSpecularScale);
    }

    private static void Check(string label, string name, double? value, double min, double max)
    {
        if (value is { } v && (!double.IsFinite(v) || v < min || v > max))
        {
            throw new JsonException($"{label}: 'look.{name}' must be between {min:0.###} and {max:0.###}, got {v}.");
        }
    }
}
