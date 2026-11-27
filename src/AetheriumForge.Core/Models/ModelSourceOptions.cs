using System.Net.Http.Headers;
using System.Reflection;
using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Where models are discovered and installed (IMPLEMENTATION_GUIDE §9, discovery contract). The defaults point at this repository.
/// </summary>
public sealed class ModelSourceOptions
{
    /// <summary>The name of the manifest asset in a <c>models-vN</c> release.</summary>
    public const string ManifestAssetName = "models.json";

    /// <summary>Gets or sets the GitHub repository owner.</summary>
    public string Owner { get; set; } = "panagiotispsichopedas";

    /// <summary>Gets or sets the GitHub repository name.</summary>
    public string Repo { get; set; } = "aetherium-forge";

    /// <summary>Gets or sets the release tag used when the GitHub API cannot be used. Default <c>models-v1</c>.</summary>
    public string FallbackTag { get; set; } = "models-v1";

    /// <summary>Gets or sets the models folder. Default <see cref="ModelCatalog.DefaultModelsFolder"/>.</summary>
    public string ModelsFolder { get; set; } = ModelCatalog.DefaultModelsFolder;

    /// <summary>
    /// Gets or sets the manifest cache file. <see langword="null"/> (the default) means <c>models-manifest.cache.json</c>
    /// in the parent of <see cref="ModelsFolder"/>.
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>Gets or sets the GitHub REST API base address. Default <c>https://api.github.com/</c>.</summary>
    public Uri ApiBaseAddress { get; set; } = new("https://api.github.com/");

    /// <summary>Gets or sets the GitHub web base address used for direct release downloads. Default <c>https://github.com/</c>.</summary>
    public Uri WebBaseAddress { get; set; } = new("https://github.com/");

    /// <summary>
    /// Gets or sets the <c>User-Agent</c> product token. Default <c>AetheriumForge/&lt;version&gt;</c>; it carries no personal data.
    /// </summary>
    public string UserAgent { get; set; } = DefaultUserAgent();

    /// <summary>Gets the resolved manifest cache path.</summary>
    /// <returns>The cache file path.</returns>
    public string ResolveCachePath()
    {
        if (!string.IsNullOrWhiteSpace(CachePath))
        {
            return CachePath;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(ModelsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        return Path.Combine(parent ?? ModelsFolder, "models-manifest.cache.json");
    }

    /// <summary>Gets the releases listing URL.</summary>
    /// <returns><c>{api}/repos/{owner}/{repo}/releases?per_page=100</c>.</returns>
    public Uri ReleasesUrl() => new(ApiBaseAddress, $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}/releases?per_page=100");

    /// <summary>Gets the direct download URL of an asset of a release.</summary>
    /// <param name="tag">The release tag.</param>
    /// <param name="assetName">The asset name.</param>
    /// <returns><c>{web}/{owner}/{repo}/releases/download/{tag}/{asset}</c>.</returns>
    public Uri ReleaseAssetUrl(string tag, string assetName) =>
        new(WebBaseAddress, $"{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");

    internal ProductInfoHeaderValue UserAgentHeader() =>
        ProductInfoHeaderValue.TryParse(UserAgent, out var parsed) ? parsed : new ProductInfoHeaderValue("AetheriumForge", "0");

    private static string DefaultUserAgent()
    {
        var version = typeof(ModelSourceOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ModelSourceOptions).Assembly.GetName().Version?.ToString()
            ?? "0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return $"AetheriumForge/{(plus >= 0 ? version[..plus] : version)}";
    }
}
