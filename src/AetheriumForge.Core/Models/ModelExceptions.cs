namespace AetheriumForge.Core.Models;

/// <summary>
/// Thrown when a <c>models.json</c> manifest is malformed or contains an invalid model entry.
/// </summary>
public class ModelManifestException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelManifestException"/> class.</summary>
    public ModelManifestException()
        : base("The model manifest is invalid.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelManifestException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ModelManifestException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelManifestException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ModelManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a manifest declares a <c>schemaVersion</c> this build does not support. Updating the app fixes it.
/// </summary>
public sealed class UnsupportedManifestSchemaException : ModelManifestException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedManifestSchemaException"/> class.</summary>
    public UnsupportedManifestSchemaException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UnsupportedManifestSchemaException"/> class.</summary>
    /// <param name="schemaVersion">The schema version found.</param>
    public UnsupportedManifestSchemaException(int schemaVersion)
        : base($"Model manifest schema version {schemaVersion} is not supported (this build supports {ModelManifest.SupportedSchemaVersion}). Update Aetherium Forge.")
    {
        SchemaVersion = schemaVersion;
    }

    /// <summary>Initializes a new instance of the <see cref="UnsupportedManifestSchemaException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public UnsupportedManifestSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UnsupportedManifestSchemaException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public UnsupportedManifestSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the schema version found in the manifest.</summary>
    public int SchemaVersion { get; }
}

/// <summary>
/// Thrown when no manifest can be obtained: the GitHub API, the direct download and the cache all failed.
/// </summary>
public sealed class ModelSourceUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelSourceUnavailableException"/> class.</summary>
    public ModelSourceUnavailableException()
        : base("The model list is unavailable: GitHub could not be reached and there is no cached copy.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSourceUnavailableException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ModelSourceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSourceUnavailableException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The last network error.</param>
    public ModelSourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a download URL or the host a request was redirected to is not a trusted release source
/// (<see cref="ModelSourceOptions.IsReleaseAssetUrl"/>, <see cref="ModelSourceOptions.IsTrustedDownloadUrl"/>). Nothing was written.
/// </summary>
public sealed class UntrustedModelSourceException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="UntrustedModelSourceException"/> class.</summary>
    public UntrustedModelSourceException()
        : base("The download source is not trusted.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UntrustedModelSourceException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public UntrustedModelSourceException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UntrustedModelSourceException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public UntrustedModelSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the rejected URL, when known.</summary>
    public Uri? Url { get; init; }
}

/// <summary>
/// Thrown when a downloaded model does not match the manifest. The partial file has been deleted.
/// </summary>
public class ModelVerificationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelVerificationException"/> class.</summary>
    public ModelVerificationException()
        : base("The downloaded model does not match the manifest.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelVerificationException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ModelVerificationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelVerificationException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ModelVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the model name, when known.</summary>
    public string? ModelName { get; init; }

    /// <summary>Gets the expected value (size in bytes or SHA-256), when known.</summary>
    public string? Expected { get; init; }

    /// <summary>Gets the actual value (size in bytes or SHA-256), when known.</summary>
    public string? Actual { get; init; }
}

/// <summary>
/// Thrown when a downloaded model's size differs from the manifest.
/// </summary>
public sealed class ModelSizeMismatchException : ModelVerificationException
{
    /// <summary>Initializes a new instance of the <see cref="ModelSizeMismatchException"/> class.</summary>
    public ModelSizeMismatchException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSizeMismatchException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ModelSizeMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSizeMismatchException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ModelSizeMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a downloaded model's SHA-256 differs from the manifest.
/// </summary>
public sealed class ModelHashMismatchException : ModelVerificationException
{
    /// <summary>Initializes a new instance of the <see cref="ModelHashMismatchException"/> class.</summary>
    public ModelHashMismatchException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelHashMismatchException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public ModelHashMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelHashMismatchException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ModelHashMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
