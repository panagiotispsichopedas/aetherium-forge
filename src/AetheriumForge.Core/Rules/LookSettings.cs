using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Rules;

/// <summary>
/// Artistic direction for one group (IMPLEMENTATION_GUIDE §8, "Look"). Every value defaults to neutral, which reproduces the plain
/// model output exactly.
/// </summary>
/// <remarks>
/// The ranges below are enforced when rules are read from JSON (<see cref="ForgeRules.FromJson"/> throws <see cref="JsonException"/>
/// for a value outside them) and are exposed as constants so editors can bound their sliders.
/// </remarks>
public sealed record LookSettings
{
    /// <summary>The lowest <see cref="AiStrength"/>.</summary>
    public const double MinAiStrength = 0.0;

    /// <summary>The highest <see cref="AiStrength"/>.</summary>
    public const double MaxAiStrength = 1.0;

    /// <summary>The lowest <see cref="Denoise"/>.</summary>
    public const double MinDenoise = 0.0;

    /// <summary>The highest <see cref="Denoise"/>.</summary>
    public const double MaxDenoise = 1.0;

    /// <summary>The lowest <see cref="Brightness"/>.</summary>
    public const double MinBrightness = -1.0;

    /// <summary>The highest <see cref="Brightness"/>.</summary>
    public const double MaxBrightness = 1.0;

    /// <summary>The lowest <see cref="Contrast"/>.</summary>
    public const double MinContrast = 0.5;

    /// <summary>The highest <see cref="Contrast"/>.</summary>
    public const double MaxContrast = 2.0;

    /// <summary>The lowest <see cref="Gamma"/>.</summary>
    public const double MinGamma = 0.5;

    /// <summary>The highest <see cref="Gamma"/>.</summary>
    public const double MaxGamma = 2.0;

    /// <summary>The lowest <see cref="Saturation"/>.</summary>
    public const double MinSaturation = 0.0;

    /// <summary>The highest <see cref="Saturation"/>.</summary>
    public const double MaxSaturation = 2.0;

    /// <summary>The lowest <see cref="Temperature"/> (coolest).</summary>
    public const double MinTemperature = -1.0;

    /// <summary>The highest <see cref="Temperature"/> (warmest).</summary>
    public const double MaxTemperature = 1.0;

    /// <summary>The lowest <see cref="NormalStrength"/> (0 flattens the normal map).</summary>
    public const double MinNormalStrength = 0.0;

    /// <summary>The highest <see cref="NormalStrength"/>.</summary>
    public const double MaxNormalStrength = 4.0;

    /// <summary>The lowest <see cref="SpecularScale"/>.</summary>
    public const double MinSpecularScale = 0.0;

    /// <summary>The highest <see cref="SpecularScale"/>.</summary>
    public const double MaxSpecularScale = 2.0;

    /// <summary>Gets the neutral look (all defaults).</summary>
    public static LookSettings Neutral { get; } = new();

    /// <summary>Gets the blend of the AI output with a plain Lanczos upscale: 1 = AI only, 0 = Lanczos only. Range 0–1.</summary>
    public double AiStrength { get; init; } = 1.0;

    /// <summary>Gets the unsharp mask applied after resizing (colour kinds only).</summary>
    public SharpenSettings Sharpen { get; init; } = SharpenSettings.None;

    /// <summary>
    /// Gets the denoise strength (0–1) for models that support it, or <see langword="null"/> for the model's default.
    /// </summary>
    /// <remarks>Persisted and hashed, but not applied yet: no published model has a denoise variant (see <c>LookRenderer</c>).</remarks>
    public double? Denoise { get; init; }

    /// <summary>Gets the brightness offset in linear light, −1–1 (colour kinds only).</summary>
    public double Brightness { get; init; }

    /// <summary>Gets the contrast multiplier around linear mid-grey (0.18), 0.5–2 (colour kinds only).</summary>
    public double Contrast { get; init; } = 1.0;

    /// <summary>Gets the gamma applied to the perceptual (sRGB-encoded) value as <c>v^(1/gamma)</c>, 0.5–2; above 1 brightens mid-tones (colour kinds only).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Gets the saturation multiplier around Rec.709 luma, 0–2 (colour kinds only).</summary>
    public double Saturation { get; init; } = 1.0;

    /// <summary>Gets the colour temperature shift, −1 (cool) to 1 (warm), luminance preserving (colour kinds only).</summary>
    public double Temperature { get; init; }

    /// <summary>Gets the normal-map XY strength ("how 3D"), 0–4; 1 keeps the relief unchanged.</summary>
    public double NormalStrength { get; init; } = 1.0;

    /// <summary>Gets the specular intensity multiplier ("how shiny"), 0–2, for specular maps and the specular alpha of normal maps.</summary>
    public double SpecularScale { get; init; } = 1.0;

    /// <summary>Gets a value indicating whether every control is at its neutral value.</summary>
    [JsonIgnore]
    public bool IsNeutral => this == Neutral;

    /// <summary>
    /// Gets a stable hash of these settings for the job store (§8): canonical JSON (fixed property order, compact, shortest
    /// round-trip numbers, <c>-0</c> written as <c>0</c>) → SHA-256 → the first 16 lowercase hex digits.
    /// </summary>
    /// <returns>A 16-character hex string; equal settings always give the same hash, across runs and machines.</returns>
    public string GetStableHash()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("aiStrength", Canonical(AiStrength));
            writer.WriteStartObject("sharpen");
            writer.WriteNumber("amount", Canonical(Sharpen.Amount));
            writer.WriteNumber("radius", Canonical(Sharpen.Radius));
            writer.WriteEndObject();
            if (Denoise is { } denoise)
            {
                writer.WriteNumber("denoise", Canonical(denoise));
            }
            else
            {
                writer.WriteNull("denoise");
            }

            writer.WriteNumber("brightness", Canonical(Brightness));
            writer.WriteNumber("contrast", Canonical(Contrast));
            writer.WriteNumber("gamma", Canonical(Gamma));
            writer.WriteNumber("saturation", Canonical(Saturation));
            writer.WriteNumber("temperature", Canonical(Temperature));
            writer.WriteNumber("normalStrength", Canonical(NormalStrength));
            writer.WriteNumber("specularScale", Canonical(SpecularScale));
            writer.WriteEndObject();
        }

        byte[] digest = SHA256.HashData(buffer.WrittenSpan);
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    /// <summary>
    /// Throws <see cref="JsonException"/> when a value is outside its range or not finite.
    /// </summary>
    /// <param name="label">The owner, for example <c>Group 'Clutter'</c>, used in the message.</param>
    /// <exception cref="JsonException">A value is out of range.</exception>
    internal void Validate(string label)
    {
        if (Sharpen is null)
        {
            throw new JsonException($"{label}: 'look.sharpen' must be an object.");
        }

        CheckRange(label, "aiStrength", AiStrength, MinAiStrength, MaxAiStrength);
        CheckRange(label, "sharpen.amount", Sharpen.Amount, SharpenSettings.MinAmount, SharpenSettings.MaxAmount);
        CheckRange(label, "sharpen.radius", Sharpen.Radius, SharpenSettings.MinRadius, SharpenSettings.MaxRadius);
        if (Denoise is { } denoise)
        {
            CheckRange(label, "denoise", denoise, MinDenoise, MaxDenoise);
        }

        CheckRange(label, "brightness", Brightness, MinBrightness, MaxBrightness);
        CheckRange(label, "contrast", Contrast, MinContrast, MaxContrast);
        CheckRange(label, "gamma", Gamma, MinGamma, MaxGamma);
        CheckRange(label, "saturation", Saturation, MinSaturation, MaxSaturation);
        CheckRange(label, "temperature", Temperature, MinTemperature, MaxTemperature);
        CheckRange(label, "normalStrength", NormalStrength, MinNormalStrength, MaxNormalStrength);
        CheckRange(label, "specularScale", SpecularScale, MinSpecularScale, MaxSpecularScale);
    }

    private static void CheckRange(string label, string name, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new JsonException($"{label}: 'look.{name}' must be between {min:0.###} and {max:0.###}, got {value}.");
        }
    }

    // Adding +0.0 turns -0.0 into +0.0, so the two spellings of zero hash the same.
    private static double Canonical(double value) => value + 0.0;
}

/// <summary>
/// Unsharp-mask settings.
/// </summary>
/// <param name="Amount">Strength, 0–5; 0 disables sharpening.</param>
/// <param name="Radius">Gaussian radius (sigma) in output pixels, 0.1–10.</param>
public sealed record SharpenSettings(double Amount = 0.0, double Radius = 1.0)
{
    /// <summary>The lowest <see cref="Amount"/>.</summary>
    public const double MinAmount = 0.0;

    /// <summary>The highest <see cref="Amount"/>.</summary>
    public const double MaxAmount = 5.0;

    /// <summary>The lowest <see cref="Radius"/>.</summary>
    public const double MinRadius = 0.1;

    /// <summary>The highest <see cref="Radius"/>.</summary>
    public const double MaxRadius = 10.0;

    /// <summary>Gets the disabled setting.</summary>
    public static SharpenSettings None { get; } = new();
}
