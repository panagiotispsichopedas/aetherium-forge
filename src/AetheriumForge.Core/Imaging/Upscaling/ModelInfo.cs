namespace AetheriumForge.Core.Imaging;

/// <summary>
/// A model found in the models folder (IMPLEMENTATION_GUIDE §9).
/// </summary>
/// <param name="Name">File name without extension; the key used by rules and <see cref="UpscalerFactory.Get"/>.</param>
/// <param name="Path">Full path to the <c>.onnx</c> file.</param>
/// <param name="DisplayName">Name shown in the UI; the sidecar <c>displayName</c> or <paramref name="Name"/>.</param>
/// <param name="Scale">Scale declared by the sidecar, or <see langword="null"/>; the loaded model is authoritative.</param>
/// <param name="Precision">Tensor precision, lower case: <c>fp32</c> (default) or <c>fp16</c>.</param>
/// <param name="Licence">SPDX licence identifier from the sidecar, or <see langword="null"/> when unknown.</param>
/// <param name="RecommendedFor">Texture kinds the model suits, for example <c>diffuse</c> and <c>normal</c>.</param>
public sealed record ModelInfo(string Name, string Path, string DisplayName, int? Scale, string Precision, string? Licence, IReadOnlyList<string> RecommendedFor);
