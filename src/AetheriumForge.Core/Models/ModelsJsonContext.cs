using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Source-generated JSON metadata for the models module: camelCase names, unknown fields ignored, comments and trailing commas accepted.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ManifestHeader))]
[JsonSerializable(typeof(ModelManifest))]
[JsonSerializable(typeof(InstalledModelSidecar))]
[JsonSerializable(typeof(ManifestCacheEntry))]
[JsonSerializable(typeof(PartialDownloadMeta))]
internal sealed partial class ModelsJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated JSON metadata for the GitHub REST API, which uses snake_case names.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IReadOnlyList<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

/// <summary>One release from <c>GET /repos/{owner}/{repo}/releases</c>; only the fields used.</summary>
/// <param name="TagName">The tag.</param>
/// <param name="Draft">Whether the release is a draft.</param>
/// <param name="Assets">The release assets.</param>
internal sealed record GitHubRelease(string? TagName, bool Draft, IReadOnlyList<GitHubAsset>? Assets);

/// <summary>One release asset.</summary>
/// <param name="Name">The asset file name.</param>
/// <param name="BrowserDownloadUrl">The public download URL.</param>
internal sealed record GitHubAsset(string? Name, Uri? BrowserDownloadUrl);

/// <summary>The cached manifest with the validators needed for conditional requests, and the last discovery result.</summary>
internal sealed record ManifestCacheEntry
{
    /// <summary>Gets the URL the manifest was downloaded from; <see cref="ETag"/> is only valid for it.</summary>
    public Uri? Url { get; init; }

    /// <summary>Gets the manifest entity tag.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets when the manifest was downloaded or revalidated.</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Gets the manifest JSON text.</summary>
    public string? Content { get; init; }

    /// <summary>Gets the entity tag of the first release-list page.</summary>
    public string? ReleasesETag { get; init; }

    /// <summary>Gets the last discovered <c>models-vN</c> tag.</summary>
    public string? DiscoveredTag { get; init; }

    /// <summary>Gets the manifest URL of <see cref="DiscoveredTag"/>.</summary>
    public Uri? DiscoveredUrl { get; init; }

    /// <summary>Gets when the release list was last read or revalidated.</summary>
    public DateTimeOffset? DiscoveredAt { get; init; }
}

/// <summary>Written next to a partial download so a resume only continues the same file.</summary>
/// <param name="Url">The model URL.</param>
/// <param name="Sha256">The expected SHA-256 of the complete file.</param>
/// <param name="Size">The expected size.</param>
/// <param name="ETag">The strong entity tag of the first response, for <c>If-Range</c>.</param>
/// <param name="LastModified">The <c>Last-Modified</c> of the first response, for <c>If-Range</c> when there is no strong ETag.</param>
internal sealed record PartialDownloadMeta(Uri Url, string Sha256, long Size, string? ETag, DateTimeOffset? LastModified);