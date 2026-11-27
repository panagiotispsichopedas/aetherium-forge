using System.Runtime.InteropServices;

namespace AetheriumForge.Native.Interop;

/// <summary>
/// The minimal Direct3D 12 surface needed to read <c>D3D12_FEATURE_DATA_ARCHITECTURE.UMA</c> for an adapter.
/// </summary>
/// <remarks>
/// Values and the vtable slot come from the Windows SDK 10.0.26100.0 headers (<c>um/d3d12.h</c>, <c>um/d3dcommon.h</c>):
/// <c>ID3D12Device</c> : <c>ID3D12Object</c> (GetPrivateData 3, SetPrivateData 4, SetPrivateDataInterface 5, SetName 6) :
/// <c>IUnknown</c>; then GetNodeCount (7) … CheckFeatureSupport (13).
/// </remarks>
internal static unsafe partial class D3D12Native
{
    /// <summary><c>D3D_FEATURE_LEVEL_11_0</c>, the minimum level D3D12 accepts.</summary>
    public const int FeatureLevel110 = 0xb000;

    /// <summary><c>D3D12_FEATURE_ARCHITECTURE</c>.</summary>
    public const int FeatureArchitecture = 1;

    private const int SlotCheckFeatureSupport = 13;

    /// <summary>Gets <c>IID_ID3D12Device</c>.</summary>
    public static Guid IidDevice { get; } = new("189819f1-1db6-4b57-be54-1821339b85f7");

    /// <summary><c>HRESULT D3D12CreateDevice(IUnknown *pAdapter, D3D_FEATURE_LEVEL MinimumFeatureLevel, REFIID riid, void **ppDevice)</c>.</summary>
    [LibraryImport("d3d12.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int D3D12CreateDevice(void* adapter, int minimumFeatureLevel, Guid* riid, void** device);

    /// <summary><c>ID3D12Device::CheckFeatureSupport(D3D12_FEATURE Feature, void *pFeatureSupportData, UINT FeatureSupportDataSize)</c>.</summary>
    public static int CheckFeatureSupport(void* device, int feature, void* data, uint size) =>
        ((delegate* unmanaged[Stdcall]<void*, int, void*, uint, int>)(*(void***)device)[SlotCheckFeatureSupport])(device, feature, data, size);
}

/// <summary>
/// <c>D3D12_FEATURE_DATA_ARCHITECTURE</c>: <c>UINT NodeIndex</c> then three <c>BOOL</c>, 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FeatureDataArchitecture
{
    /// <summary><c>UINT NodeIndex</c> (input).</summary>
    public uint NodeIndex;

    /// <summary><c>BOOL TileBasedRenderer</c>.</summary>
    public int TileBasedRenderer;

    /// <summary><c>BOOL UMA</c>: the adapter shares memory with the CPU (integrated).</summary>
    public int Uma;

    /// <summary><c>BOOL CacheCoherentUMA</c>.</summary>
    public int CacheCoherentUma;
}
