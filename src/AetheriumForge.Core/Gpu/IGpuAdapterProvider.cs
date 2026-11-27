namespace AetheriumForge.Core.Gpu;

/// <summary>
/// Enumerates GPU adapters and reads their video-memory budget (IMPLEMENTATION_GUIDE §9, "GPU auto profile").
/// The Windows implementation lives in <c>AetheriumForge.Native</c> (DXGI).
/// </summary>
public interface IGpuAdapterProvider
{
    /// <summary>
    /// Lists hardware and software adapters, high-performance first.
    /// </summary>
    /// <returns>The adapters; empty when none can be enumerated.</returns>
    IReadOnlyList<GpuAdapterInfo> GetAdapters();

    /// <summary>
    /// Reads the current local (dedicated) video-memory budget and usage of an adapter.
    /// </summary>
    /// <param name="luid">The adapter's LUID (<see cref="GpuAdapterInfo.Luid"/>).</param>
    /// <returns>The memory info, or <see langword="null"/> when the adapter is gone or does not report it.</returns>
    VideoMemoryInfo? QueryVideoMemory(long luid);
}

/// <summary>
/// One GPU adapter as reported by DXGI.
/// </summary>
/// <param name="Luid">Locally unique id; stable until reboot, identifies the adapter for memory queries.</param>
/// <param name="DirectMLDeviceId">The DXGI <c>EnumAdapters1</c> index, which is the <c>deviceId</c> ONNX Runtime's DirectML provider expects.</param>
/// <param name="Name">Adapter description, for example <c>NVIDIA GeForce GTX 1080</c>.</param>
/// <param name="VendorId">PCI vendor id (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel).</param>
/// <param name="DeviceId">PCI device id.</param>
/// <param name="DedicatedVideoMemory">Dedicated video memory in bytes.</param>
/// <param name="SharedSystemMemory">Shared system memory in bytes.</param>
/// <param name="IsIntegrated">Whether the adapter is integrated (uses system memory; DXGI reports it as non-local or UMA).</param>
/// <param name="IsSoftware">Whether this is a software adapter (for example Microsoft Basic Render Driver).</param>
/// <param name="DriverVersion">The user-mode driver version, when available; part of the profile key.</param>
public sealed record GpuAdapterInfo(
    long Luid,
    int DirectMLDeviceId,
    string Name,
    int VendorId,
    int DeviceId,
    ulong DedicatedVideoMemory,
    ulong SharedSystemMemory,
    bool IsIntegrated,
    bool IsSoftware,
    string? DriverVersion);

/// <summary>
/// Local video-memory state of an adapter (<c>DXGI_QUERY_VIDEO_MEMORY_INFO</c>, local segment group).
/// </summary>
/// <param name="Budget">Bytes the OS lets this process use.</param>
/// <param name="CurrentUsage">Bytes this process currently uses.</param>
/// <param name="AvailableForReservation">Bytes available for reservation.</param>
/// <param name="CurrentReservation">Bytes currently reserved.</param>
public sealed record VideoMemoryInfo(ulong Budget, ulong CurrentUsage, ulong AvailableForReservation, ulong CurrentReservation)
{
    /// <summary>Gets the budget left for this process (<see cref="Budget"/> − <see cref="CurrentUsage"/>, floored at 0).</summary>
    public ulong Available => Budget > CurrentUsage ? Budget - CurrentUsage : 0;
}
