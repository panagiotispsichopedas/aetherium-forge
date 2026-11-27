namespace AetheriumForge.Core.Game;

/// <summary>
/// An archive that provides a resource.
/// </summary>
/// <param name="ArchiveName">The archive file name, for example <c>Skyrim - Textures0.bsa</c>.</param>
public sealed record ResourceSource(string ArchiveName);
