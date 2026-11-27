namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Options for <see cref="OnnxModelRunner"/>.
/// </summary>
public sealed record OnnxRunnerOptions
{
    /// <summary>Gets the DXGI adapter index used by the DirectML execution provider. Default 0.</summary>
    public int DeviceId { get; init; }

    /// <summary>Gets a value indicating whether to use the DirectML execution provider; <see langword="false"/> selects the CPU provider. Default <see langword="true"/>.</summary>
    public bool UseDirectML { get; init; } = true;
}
