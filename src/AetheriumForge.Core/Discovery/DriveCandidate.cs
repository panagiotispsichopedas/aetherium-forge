namespace AetheriumForge.Core.Discovery;

/// <summary>
/// A fixed drive considered for the project (working) folder.
/// </summary>
/// <param name="RootPath">Drive root, for example <c>D:\</c>.</param>
/// <param name="FreeBytes">Free space available to the current user.</param>
/// <param name="IsSystemDrive"><see langword="true"/> for the drive that holds Windows.</param>
/// <param name="Fits"><see langword="true"/> when <paramref name="FreeBytes"/> covers the required space.</param>
public sealed record DriveCandidate(string RootPath, long FreeBytes, bool IsSystemDrive, bool Fits);
