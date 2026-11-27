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

    /// <summary>
    /// Gets the hosts a release download may be redirected to, besides the host of <see cref="WebBaseAddress"/>. Default: GitHub's
    /// release asset hosts <c>objects.githubusercontent.com</c> and <c>release-assets.githubusercontent.com</c>.
    /// </summary>
    public IList<string> AllowedDownloadHosts { get; } = ["objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Gets or sets how long a download may receive no data before it fails (the partial is kept). Default 60 seconds.</summary>
    public TimeSpan DownloadIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets how long a discovered release is reused without asking the GitHub API again. Default 10 minutes.</summary>
    public TimeSpan DiscoveryCacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the most release-list pages to read while looking for a <c>models-vN</c> tag. Default 10.</summary>
    public int MaxReleasePages { get; set; } = 10;

    /// <summary>Gets or sets the clock used for cache ages. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="url"/> is <c>https://{web host}/{owner}/{repo}/releases/download/{tag}/{asset}</c>
    /// with no user info, port, query or extra path segments.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <returns><see langword="true"/> when the URL is a release asset of this repository.</returns>
    public bool IsReleaseAssetUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri
            || url.Scheme != Uri.UriSchemeHttps
            || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || !string.Equals(url.Host, WebBaseAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = $"/{Owner}/{Repo}/releases/download/";
        var path = url.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path[prefix.Length..].Split('/');
        return rest.Length == 2 && rest.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    /// <summary>
    /// Returns <see langword="true"/> when a response that ended at <paramref name="url"/> (after redirects) is trusted: https on the
    /// <see cref="WebBaseAddress"/> host or one of <see cref="AllowedDownloadHosts"/>.
    /// </summary>
    /// <param name="url">The final request URL.</param>
    /// <returns><see langword="true"/> when trusted.</returns>
    public bool IsTrustedDownloadUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsAbsoluteUri
            && url.Scheme == Uri.UriSchemeHttps
            && (string.Equals(url.Host, WebBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
                || AllowedDownloadHosts.Any(host => string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase)));
    }

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
