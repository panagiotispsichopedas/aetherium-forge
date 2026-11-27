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

/// <summary>The cached manifest with the validators needed for a conditional request.</summary>
/// <param name="Url">The URL the manifest was downloaded from; the ETag is only valid for it.</param>
/// <param name="ETag">The entity tag, or <see langword="null"/>.</param>
/// <param name="FetchedAt">When it was downloaded.</param>
/// <param name="Content">The manifest JSON text.</param>
internal sealed record ManifestCacheEntry(Uri Url, string? ETag, DateTimeOffset FetchedAt, string Content);
