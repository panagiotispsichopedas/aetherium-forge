using AetheriumForge.Core.Gpu;

namespace AetheriumForge.App.Services;

/// <summary>
/// One model offered for download, as shown in a confirmation: name, size and licence.
/// </summary>
/// <param name="DisplayName">The display name.</param>
/// <param name="Size">The download size in bytes.</param>
/// <param name="Licence">The SPDX licence identifier, or <see langword="null"/> when the manifest names none.</param>
/// <param name="LicenceUrl">Where the licence text can be read, or <see langword="null"/>.</param>
public sealed record ModelDownloadItem(string DisplayName, long Size, string? Licence, Uri? LicenceUrl);

/// <summary>
/// The first-run recommendation: the model family for this GPU's tier and what downloading it takes.
/// </summary>
/// <param name="AdapterName">The selected adapter's name, or <see langword="null"/> when no hardware adapter was found.</param>
/// <param name="Tier">The adapter tier.</param>
/// <param name="Family">The recommended model family.</param>
/// <param name="Items">The family's downloadable variants.</param>
public sealed record ModelFirstRunRecommendation(string? AdapterName, GpuTier Tier, string Family, IReadOnlyList<ModelDownloadItem> Items);

/// <summary>
/// The user's answer to the first-run prompt.
/// </summary>
public enum ModelFirstRunChoice
{
    /// <summary>Not now.</summary>
    Later,

    /// <summary>Download the recommended family.</summary>
    DownloadRecommended,

    /// <summary>Open the Models page to pick models.</summary>
    Choose,
}

/// <summary>
/// The dialogs the Models page shows. The Avalonia implementation shows modal windows; tests answer with fakes.
/// </summary>
public interface IModelDialogs
{
    /// <summary>Asks the user to confirm a download after showing the size and licence of each model.</summary>
    /// <param name="items">The models to download.</param>
    /// <returns><see langword="true"/> to download.</returns>
    Task<bool> ConfirmDownloadAsync(IReadOnlyList<ModelDownloadItem> items);

    /// <summary>Asks the user to confirm deleting a model.</summary>
    /// <param name="displayName">The model's display name.</param>
    /// <param name="partialBytes">The size of a partial download that is deleted too, or <see langword="null"/> when there is none.</param>
    /// <returns><see langword="true"/> to delete.</returns>
    Task<bool> ConfirmDeleteAsync(string displayName, long? partialBytes);

    /// <summary>Asks the user to confirm discarding an interrupted download.</summary>
    /// <param name="displayName">The model's display name.</param>
    /// <param name="partialBytes">The size of the partial file.</param>
    /// <returns><see langword="true"/> to discard it.</returns>
    Task<bool> ConfirmDiscardPartialAsync(string displayName, long partialBytes);

    /// <summary>Shows the first-run prompt.</summary>
    /// <param name="recommendation">The recommendation.</param>
    /// <returns>The user's choice.</returns>
    Task<ModelFirstRunChoice> ShowFirstRunAsync(ModelFirstRunRecommendation recommendation);

    /// <summary>Offers to calibrate a model family that was just downloaded.</summary>
    /// <param name="family">The model family.</param>
    /// <returns><see langword="true"/> to calibrate now.</returns>
    Task<bool> OfferCalibrationAsync(string family);

    /// <summary>Lets the user pick a custom <c>.onnx</c> file.</summary>
    /// <returns>The file path, or <see langword="null"/> when cancelled.</returns>
    Task<string?> PickModelFileAsync();
}
