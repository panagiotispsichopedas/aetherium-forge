namespace AetheriumForge.Core.Discovery;

/// <summary>
/// A validated Skyrim Special Edition installation.
/// </summary>
/// <param name="GameFolder">Absolute game folder that contains <c>SkyrimSE.exe</c>.</param>
/// <param name="DataFolder">Absolute <c>Data</c> folder inside <paramref name="GameFolder"/>.</param>
/// <param name="Store">How the installation was found.</param>
/// <param name="ExeVersion">File version of <c>SkyrimSE.exe</c> (for example 1.6.1170.0), or <see langword="null"/> when it has none.</param>
public sealed record GameInstall(string GameFolder, string DataFolder, GameStore Store, Version? ExeVersion);
