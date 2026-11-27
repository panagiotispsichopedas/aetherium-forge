namespace AetheriumForge.Core.Archives;

/// <summary>
/// One file record of a Skyrim SE (version 105) BSA archive.
/// </summary>
/// <param name="Path">Canonical path (<c>folder\file.ext</c>, lowercase, backslashes) produced by <see cref="Common.GamePath.Normalize"/>.</param>
/// <param name="FolderHash">The folder hash stored in the archive's folder record.</param>
/// <param name="FileHash">The file hash stored in the archive's file record.</param>
/// <param name="Size">The stored size of the data block with the <c>0x40000000</c> compression toggle bit removed.</param>
/// <param name="Offset">Absolute offset of the data block from the start of the archive.</param>
/// <param name="IsCompressed">Whether the data block is compressed (archive default combined with the per-file toggle bit).</param>
public sealed record BsaEntry(string Path, ulong FolderHash, ulong FileHash, uint Size, uint Offset, bool IsCompressed);
