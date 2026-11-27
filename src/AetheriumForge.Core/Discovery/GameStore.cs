namespace AetheriumForge.Core.Discovery;

/// <summary>
/// The storefront (or manual selection) a Skyrim Special Edition installation was found through.
/// </summary>
/// <remarks>The store decides the profile folder suffix (IMPLEMENTATION_GUIDE §4.2).</remarks>
public enum GameStore
{
    /// <summary>Installed through Steam (app id 489830).</summary>
    Steam,

    /// <summary>Installed through the Bethesda.net launcher (registry value <c>installed path</c>).</summary>
    Bethesda,

    /// <summary>Installed through GOG Galaxy.</summary>
    Gog,

    /// <summary>Installed through the Epic Games Launcher.</summary>
    Epic,

    /// <summary>Folder chosen by the user.</summary>
    Manual,
}
