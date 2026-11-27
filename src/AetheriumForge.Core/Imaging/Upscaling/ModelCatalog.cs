using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Finds models in a models folder: <c>*.onnx</c> plus an optional <c>&lt;name&gt;.json</c> sidecar (IMPLEMENTATION_GUIDE §9).
/// </summary>
public static partial class ModelCatalog
{
    /// <summary>The precision assumed when no sidecar declares one.</summary>
    public const string DefaultPrecision = "fp32";

    /// <summary>Gets the default models folder, <c>%LOCALAPPDATA%\AetheriumForge\models</c>.</summary>
    public static string DefaultModelsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "models");

    /// <summary>
    /// Lists the models in <paramref name="modelsFolder"/>, sorted by name. A missing folder yields an empty list.
    /// </summary>
    /// <param name="modelsFolder">The folder to search (top level only).</param>
    /// <returns>The models found.</returns>
    public static IReadOnlyList<ModelInfo> Discover(string modelsFolder) => Discover(modelsFolder, NullLogger.Instance);

    /// <summary>
    /// Lists the models in <paramref name="modelsFolder"/>, sorted by name. A missing folder yields an empty list. An unreadable
    /// or invalid sidecar is logged as a warning and the model keeps its defaults.
    /// </summary>
    /// <param name="modelsFolder">The folder to search (top level only).</param>
    /// <param name="logger">Receives sidecar warnings.</param>
    /// <returns>The models found.</returns>
    public static IReadOnlyList<ModelInfo> Discover(string modelsFolder, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Directory.Exists(modelsFolder))
        {
            return [];
        }

        var models = new List<ModelInfo>();
        foreach (var modelPath in Directory.EnumerateFiles(modelsFolder, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            // EnumerateFiles also matches longer extensions such as ".onnx_data" with the 8.3 pattern rules.
            if (!string.Equals(Path.GetExtension(modelPath), ".onnx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(modelPath);
            var sidecar = ReadSidecar(Path.ChangeExtension(modelPath, ".json"), logger);
            models.Add(new ModelInfo(
                name,
                Path.GetFullPath(modelPath),
                string.IsNullOrWhiteSpace(sidecar?.DisplayName) ? name : sidecar.DisplayName,
                sidecar?.Scale is >= 1 ? sidecar.Scale : null,
                string.IsNullOrWhiteSpace(sidecar?.Precision) ? DefaultPrecision : sidecar.Precision.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(sidecar?.Licence) ? null : sidecar.Licence,
                sidecar?.RecommendedFor?.Where(kind => !string.IsNullOrWhiteSpace(kind)).ToArray() ?? []));
        }

        models.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return models;
    }

    private static ModelSidecar? ReadSidecar(string sidecarPath, ILogger logger)
    {
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(sidecarPath);
            return JsonSerializer.Deserialize(stream, ModelSidecarJsonContext.Default.ModelSidecar);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LogInvalidSidecar(logger, exception, sidecarPath);
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Ignoring unreadable model sidecar {SidecarPath}; using defaults")]
    private static partial void LogInvalidSidecar(ILogger logger, Exception exception, string sidecarPath);
}
