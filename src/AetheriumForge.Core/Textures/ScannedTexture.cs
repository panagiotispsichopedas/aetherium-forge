using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Textures;

/// <summary>
/// One texture of the gap set together with its header and the rule decision for it.
/// </summary>
/// <param name="Path">Canonical resource path, for example <c>textures\clutter\bucket01.dds</c>.</param>
/// <param name="SourceArchive">File name of the base-game archive that provides the texture.</param>
/// <param name="Header">The parsed DDS header, or <see langword="null"/> when it could not be read.</param>
/// <param name="Decision">What the rules decided for this texture.</param>
public sealed record ScannedTexture(string Path, string SourceArchive, DdsHeader? Header, TextureDecision Decision);
