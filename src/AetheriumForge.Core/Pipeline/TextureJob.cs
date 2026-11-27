using AetheriumForge.Core.Textures;

namespace AetheriumForge.Core.Pipeline;

/// <summary>
/// Everything needed to process one texture (IMPLEMENTATION_GUIDE §10).
/// </summary>
/// <param name="Path">Canonical resource path, for example <c>textures\clutter\bucket01.dds</c>.</param>
/// <param name="SourceArchive">Archive that provides the original texture.</param>
/// <param name="Kind">Texture kind; decides normal-map handling and whether the AI model runs.</param>
/// <param name="TargetWidth">Output width in pixels.</param>
/// <param name="TargetHeight">Output height in pixels.</param>
/// <param name="Model">Upscaling model name.</param>
/// <param name="NormalStrength">Normal-map XY strength; 1.0 keeps the relief unchanged.</param>
/// <param name="SourceDxgiFormat">DXGI format of the source when it has a DX10 header; used to keep sRGB tagging.</param>
public sealed record TextureJob(
    string Path,
    string SourceArchive,
    TextureKind Kind,
    int TargetWidth,
    int TargetHeight,
    string Model,
    double NormalStrength,
    int? SourceDxgiFormat = null);

/// <summary>
/// Outcome of <see cref="TextureProcessor.ProcessAsync"/>.
/// </summary>
/// <param name="OutputPath">The committed BC7 DDS file.</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="HadAlpha">Whether the source alpha channel was kept.</param>
/// <param name="UsedModel">Whether the AI model ran (false for parallax/height maps).</param>
/// <param name="Timings">Time spent in each stage.</param>
public sealed record TextureResult(string OutputPath, int Width, int Height, bool HadAlpha, bool UsedModel, StageTimings Timings);

/// <summary>
/// Time spent per processing stage; feeds the ETA and the Run screen's stage lanes.
/// </summary>
/// <param name="Extract">Reading the source from its archive.</param>
/// <param name="Decode">texconv decode.</param>
/// <param name="Upscale">AI upscale (zero when the model was not used).</param>
/// <param name="PostProcess">Resize, normal renormalisation and alpha merge.</param>
/// <param name="Encode">texconv BC7 encode and commit.</param>
public sealed record StageTimings(TimeSpan Extract, TimeSpan Decode, TimeSpan Upscale, TimeSpan PostProcess, TimeSpan Encode)
{
    /// <summary>Gets the total time.</summary>
    public TimeSpan Total => Extract + Decode + Upscale + PostProcess + Encode;
}
