using AetheriumForge.Core.Discovery;
using AetheriumForge.Core.Projects;

namespace AetheriumForge.App.Pages.Scan;

/// <summary>
/// One detected (or browsed) game install in the Scan page's list.
/// </summary>
/// <param name="Install">The validated install.</param>
public sealed record GameInstallItem(GameInstall Install)
{
    /// <summary>Gets the store the install was found through.</summary>
    public string StoreText => Install.Store == GameStore.Manual ? "Chosen folder" : Install.Store.ToString();

    /// <summary>Gets the game folder.</summary>
    public string GameFolder => Install.GameFolder;

    /// <summary>Gets the executable version, or <c>unknown version</c>.</summary>
    public string VersionText => Install.ExeVersion is { } version ? $"SkyrimSE.exe {version}" : "unknown version";
}

/// <summary>
/// One fixed drive with its free space.
/// </summary>
/// <param name="Root">The drive root, for example <c>D:\</c>.</param>
/// <param name="FreeBytes">Free bytes.</param>
/// <param name="IsSystem">Whether it is the Windows drive.</param>
/// <param name="HoldsProject">Whether the chosen project folder is on it.</param>
public sealed record DriveItem(string Root, long FreeBytes, bool IsSystem, bool HoldsProject)
{
    /// <summary>Gets the free space text.</summary>
    public string FreeText => $"{ProjectLocator.FormatBytes(FreeBytes)} free";

    /// <summary>Gets notes about the drive (system drive, project drive).</summary>
    public string Notes => string.Join(", ", new[] { IsSystem ? "system drive" : null, HoldsProject ? "project folder" : null }.OfType<string>());
}
