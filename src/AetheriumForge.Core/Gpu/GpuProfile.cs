namespace AetheriumForge.Core.Gpu;

/// <summary>
/// The calibrated settings of one model family on one GPU adapter (IMPLEMENTATION_GUIDE §9, "GPU auto profile").
/// Produced by <see cref="GpuProfiler.CalibrateAsync"/> and persisted by <see cref="GpuProfileStore"/>.
/// </summary>
/// <param name="AdapterLuid">The adapter LUID at calibration time. It changes on every reboot, so it is not part of the store key.</param>
/// <param name="AdapterName">The adapter description, for example <c>NVIDIA GeForce GTX 1080</c>.</param>
/// <param name="DriverVersion">The driver version at calibration time; a different driver triggers recalibration.</param>
/// <param name="DirectMLDeviceId">The DXGI adapter index passed to the DirectML execution provider.</param>
/// <param name="Model">The model family, for example <c>realesrgan-x4plus</c> (see <see cref="GpuProfiler.GetModelFamily"/>).</param>
/// <param name="Precision">The faster precision on this adapter: <c>fp32</c> or <c>fp16</c>.</param>
/// <param name="TileSize">The tile size in source pixels, excluding overlap (<c>TilingOptions.TileSize</c>).</param>
/// <param name="SecondsPerMegapixel">Measured seconds per source megapixel at <paramref name="TileSize"/>; seeds the ETA (§11).</param>
/// <param name="Tier">The adapter tier.</param>
/// <param name="RecommendedModelFamily">The model family recommended for <paramref name="Tier"/>.</param>
/// <param name="CalibratedAt">When the calibration finished.</param>
public sealed record GpuProfile(
    long AdapterLuid,
    string AdapterName,
    string? DriverVersion,
    int DirectMLDeviceId,
    string Model,
    string Precision,
    int TileSize,
    double SecondsPerMegapixel,
    GpuTier Tier,
    string RecommendedModelFamily,
    DateTimeOffset CalibratedAt)
{
    /// <summary>Gets the PCI vendor id of the adapter; part of the LUID-independent store key.</summary>
    public int VendorId { get; init; }

    /// <summary>Gets the PCI device id of the adapter; part of the LUID-independent store key.</summary>
    public int DeviceId { get; init; }

    /// <summary>
    /// Returns <see langword="true"/> when this profile was calibrated on the same kind of adapter (vendor, device and name),
    /// ignoring the LUID and the driver version.
    /// </summary>
    /// <param name="adapter">The adapter to compare with.</param>
    /// <returns><see langword="true"/> when the hardware identity matches.</returns>
    public bool MatchesHardware(GpuAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return VendorId == adapter.VendorId
            && DeviceId == adapter.DeviceId
            && string.Equals(AdapterName, adapter.Name, StringComparison.OrdinalIgnoreCase);
    }
}
