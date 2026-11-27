using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetheriumForge.Core.Models;

/// <summary>
/// The <c>models.json</c> manifest published with each <c>models-vN</c> release (IMPLEMENTATION_GUIDE §9, discovery contract).
/// Unknown fields are ignored; an unsupported <see cref="SchemaVersion"/> is refused.
/// </summary>
/// <param name="SchemaVersion">The manifest schema version; only <see cref="SupportedSchemaVersion"/> is accepted.</param>
/// <param name="Release">The release tag, for example <c>models-v1</c>.</param>
/// <param name="Models">The downloadable models.</param>
public sealed partial record ModelManifest(int SchemaVersion, string Release, IReadOnlyList<ManifestModel> Models)
{
    /// <summary>The only manifest schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Gets where this copy of the manifest came from. Not part of the JSON.</summary>
    [JsonIgnore]
    public ManifestOrigin Origin { get; init; } = ManifestOrigin.Network;

    /// <summary>
    /// Parses and validates a manifest.
    /// </summary>
    /// <param name="utf8Json">The UTF-8 JSON text.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="UnsupportedManifestSchemaException">The <c>schemaVersion</c> is not <see cref="SupportedSchemaVersion"/>.</exception>
    /// <exception cref="ModelManifestException">The JSON is malformed or a model entry is invalid.</exception>
    public static ModelManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        ManifestHeader? header;
        try
        {
            header = JsonSerializer.Deserialize(utf8Json, ModelsJsonContext.Default.ManifestHeader);
        }
        catch (JsonException exception)
        {
            throw new ModelManifestException("The model manifest is not valid JSON.", exception);
        }

        if (header is null || header.SchemaVersion is null)
        {
            throw new ModelManifestException("The model manifest has no schemaVersion.");
        }

        if (header.SchemaVersion != SupportedSchemaVersion)
        {
            throw new UnsupportedManifestSchemaException(header.SchemaVersion.Value);
        }

        ModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(utf8Json, ModelsJsonContext.Default.ModelManifest);
        }
        catch (JsonException exception)
        {
            throw new ModelManifestException("The model manifest does not match schema version 1.", exception);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Release) || manifest.Models is null)
        {
            throw new ModelManifestException("The model manifest is missing release or models.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in manifest.Models)
        {
            Validate(model);
            if (!names.Add(model.Name))
            {
                throw new ModelManifestException($"The model manifest lists '{model.Name}' twice.");
            }
        }

        return manifest;
    }

    /// <summary>
    /// Finds a model by name (case-insensitive).
    /// </summary>
    /// <param name="name">The model name.</param>
    /// <returns>The model, or <see langword="null"/>.</returns>
    public ManifestModel? Find(string name) => Models.FirstOrDefault(model => string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && fileName is not "." and not ".."
        && !fileName.EndsWith('.')
        && string.Equals(fileName.Trim(), fileName, StringComparison.Ordinal);

    private static void Validate(ManifestModel? model)
    {
        if (model is null)
        {
            throw new ModelManifestException("The model manifest contains a null model.");
        }

        if (string.IsNullOrWhiteSpace(model.Name) || !IsSafeFileName(model.Name))
        {
            throw new ModelManifestException($"The model name '{model.Name}' is missing or not a valid file name.");
        }

        if (!IsSafeFileName(model.FileName) || !string.Equals(Path.GetExtension(model.FileName), ".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelManifestException($"Model '{model.Name}' has an invalid fileName '{model.FileName}'; expected a plain *.onnx file name.");
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(model.FileName), model.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelManifestException($"Model '{model.Name}' has fileName '{model.FileName}', which does not match its name.");
        }

        if (model.Url is null || !model.Url.IsAbsoluteUri || model.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ModelManifestException($"Model '{model.Name}' has no absolute https url.");
        }

        if (model.Size <= 0)
        {
            throw new ModelManifestException($"Model '{model.Name}' has an invalid size {model.Size}.");
        }

        if (model.Sha256 is null || !Sha256Pattern().IsMatch(model.Sha256))
        {
            throw new ModelManifestException($"Model '{model.Name}' has an invalid sha256; expected 64 lowercase hex digits.");
        }

        if (model.LicenceFile is not null && !IsSafeFileName(model.LicenceFile))
        {
            throw new ModelManifestException($"Model '{model.Name}' has an invalid licenceFile '{model.LicenceFile}'.");
        }

        if (model.LicenceUrl is not null && (!model.LicenceUrl.IsAbsoluteUri || model.LicenceUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ModelManifestException($"Model '{model.Name}' has a licenceUrl that is not absolute https.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

/// <summary>
/// One downloadable model in <see cref="ModelManifest"/>.
/// </summary>
/// <param name="Name">The model key; equals <see cref="FileName"/> without <c>.onnx</c> and the <see cref="Imaging.ModelInfo.Name"/> once installed.</param>
/// <param name="Family">Groups the precisions of one model, for example FP32 and FP16.</param>
/// <param name="DisplayName">The name shown in the UI.</param>
/// <param name="Description">A one-line description.</param>
/// <param name="Architecture">The network architecture, for example <c>RRDBNet</c>.</param>
/// <param name="ParametersMillions">The parameter count in millions.</param>
/// <param name="FileName">The <c>.onnx</c> file name in the models folder.</param>
/// <param name="Url">The download URL.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Sha256">The SHA-256 of the file, lowercase hex.</param>
/// <param name="SidecarFileName">The published sidecar asset name.</param>
/// <param name="Scale">The upscale factor.</param>
/// <param name="Precision">The tensor precision: <c>fp32</c> or <c>fp16</c>.</param>
/// <param name="Tier">The recommended GPU tier: <c>standard</c> or <c>low</c>.</param>
/// <param name="Licence">The SPDX licence identifier.</param>
/// <param name="LicenceFile">The licence file name to place next to the model.</param>
/// <param name="LicenceUrl">The licence file download URL.</param>
/// <param name="RecommendedFor">The texture kinds the model suits.</param>
/// <param name="Source">Where the original weights come from.</param>
/// <param name="Input">The input tensor name.</param>
/// <param name="Output">The output tensor name.</param>
/// <param name="Opset">The ONNX opset.</param>
/// <param name="MaxAbsDiffVsPyTorch">The largest absolute difference against the PyTorch model seen during conversion.</param>
public sealed record ManifestModel(
    string Name,
    string? Family,
    string? DisplayName,
    string? Description,
    string? Architecture,
    double? ParametersMillions,
    string FileName,
    Uri Url,
    long Size,
    string Sha256,
    string? SidecarFileName,
    int? Scale,
    string? Precision,
    string? Tier,
    string? Licence,
    string? LicenceFile,
    Uri? LicenceUrl,
    IReadOnlyList<string>? RecommendedFor,
    string? Source,
    string? Input,
    string? Output,
    int? Opset,
    double? MaxAbsDiffVsPyTorch);

/// <summary>
/// Where a <see cref="ModelManifest"/> came from.
/// </summary>
public enum ManifestOrigin
{
    /// <summary>Downloaded now.</summary>
    Network,

    /// <summary>The server answered 304 Not Modified to the cached ETag.</summary>
    NotModified,

    /// <summary>The network failed; this is the cached copy.</summary>
    Cache,
}

/// <summary>Reads only <c>schemaVersion</c>, so a future schema is refused before its shape is parsed.</summary>
/// <param name="SchemaVersion">The schema version, or <see langword="null"/> when absent.</param>
internal sealed record ManifestHeader(int? SchemaVersion);
