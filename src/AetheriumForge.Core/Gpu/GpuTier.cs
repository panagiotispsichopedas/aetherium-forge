namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Performance class of a GPU adapter (IMPLEMENTATION_GUIDE §9, "GPU auto profile", step 6).
/// </summary>
public enum GpuTier
{
    /// <summary>Integrated GPU, or less than 4 GiB of dedicated video memory; a compact model is recommended.</summary>
    Low,

    /// <summary>Discrete GPU with at least 4 GiB of dedicated video memory.</summary>
    Standard,
}
