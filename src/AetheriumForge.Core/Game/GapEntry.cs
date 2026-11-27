namespace AetheriumForge.Core.Game;

/// <summary>
/// A texture in the gap set: served only by an INI archive, so Aetherium Forge processes it (IMPLEMENTATION_GUIDE §5).
/// </summary>
/// <param name="Path">The canonical resource path (lowercase, backslashes), for example <c>textures\clutter\bucket01.dds</c>.</param>
/// <param name="SourceArchive">The INI archive that provides the texture.</param>
public sealed record GapEntry(string Path, string SourceArchive);
