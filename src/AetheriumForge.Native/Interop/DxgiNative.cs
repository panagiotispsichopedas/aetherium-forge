using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AetheriumForge.Native.Interop;

/// <summary>
/// DXGI entry points, constants and hand-written COM vtable calls.
/// </summary>
/// <remarks>
/// Vtable slots and struct layouts come from the Windows SDK 10.0.26100.0 headers (<c>shared/dxgi.h</c>, <c>dxgi1_4.h</c>,
/// <c>dxgi1_6.h</c>). Slot numbers count the inherited methods: <c>IUnknown</c> (0–2), <c>IDXGIObject</c> (3–6), then each
/// derived interface in declaration order. Raw function pointers are used instead of <c>[GeneratedComInterface]</c> so every
/// reference is released deterministically, without relying on the garbage collector to finalise RCWs.
/// </remarks>
internal static unsafe partial class DxgiNative
{
    /// <summary><c>DXGI_ERROR_NOT_FOUND</c>: the enumeration index is past the last adapter.</summary>
    public const int ErrorNotFound = unchecked((int)0x887A0002);

    /// <summary><c>DXGI_ADAPTER_FLAG_SOFTWARE</c>.</summary>
    public const uint AdapterFlagSoftware = 2;

    /// <summary><c>DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE</c> (<c>UNSPECIFIED</c> = 0, <c>MINIMUM_POWER</c> = 1).</summary>
    public const int GpuPreferenceHighPerformance = 2;

    /// <summary><c>DXGI_MEMORY_SEGMENT_GROUP_LOCAL</c>.</summary>
    public const int MemorySegmentGroupLocal = 0;

    private const string DxgiLibrary = "dxgi.dll";

    // IUnknown
    private const int SlotQueryInterface = 0;
    private const int SlotRelease = 2;

    // IDXGIFactory1 : IDXGIFactory (7–11) : IDXGIObject. EnumAdapters1 follows CreateSoftwareAdapter (11).
    private const int SlotEnumAdapters1 = 12;

    // IDXGIFactory6 : IDXGIFactory5 (28) : IDXGIFactory4 (26–27) : IDXGIFactory3 (25) : IDXGIFactory2 (14–24) : IDXGIFactory1 (12–13).
    private const int SlotEnumAdapterByGpuPreference = 29;

    // IDXGIAdapter : IDXGIObject: EnumOutputs (7), GetDesc (8), CheckInterfaceSupport (9).
    private const int SlotCheckInterfaceSupport = 9;

    // IDXGIAdapter1: GetDesc1 (10).
    private const int SlotGetDesc1 = 10;

    // IDXGIAdapter3 : IDXGIAdapter2 (GetDesc2, 11): RegisterHardwareContentProtectionTeardownStatusEvent (12),
    // UnregisterHardwareContentProtectionTeardownStatus (13), QueryVideoMemoryInfo (14).
    private const int SlotQueryVideoMemoryInfo = 14;

    /// <summary>Gets <c>IID_IDXGIFactory1</c>.</summary>
    public static Guid IidFactory1 { get; } = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>Gets <c>IID_IDXGIFactory6</c>.</summary>
    public static Guid IidFactory6 { get; } = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");

    /// <summary>Gets <c>IID_IDXGIAdapter1</c>.</summary>
    public static Guid IidAdapter1 { get; } = new("29038f61-3839-4626-91fd-086879011a05");

    /// <summary>Gets <c>IID_IDXGIAdapter3</c>.</summary>
    public static Guid IidAdapter3 { get; } = new("645967a4-1392-4310-a798-8053ce3e93fd");

    /// <summary>Gets <c>IID_IDXGIDevice</c>, the interface <c>CheckInterfaceSupport</c> reports the user-mode driver version for.</summary>
    public static Guid IidDevice { get; } = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary><c>HRESULT CreateDXGIFactory2(UINT Flags, REFIID riid, void **ppFactory)</c> (Windows 8.1+).</summary>
    [LibraryImport(DxgiLibrary)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CreateDXGIFactory2(uint flags, Guid* riid, void** factory);

    /// <summary><c>HRESULT CreateDXGIFactory1(REFIID riid, void **ppFactory)</c>.</summary>
    [LibraryImport(DxgiLibrary)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CreateDXGIFactory1(Guid* riid, void** factory);

    /// <summary><c>IUnknown::QueryInterface</c>.</summary>
    public static int QueryInterface(void* instance, Guid iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(instance, SlotQueryInterface))(instance, &iid, result);

    /// <summary><c>IUnknown::Release</c>; a no-op for <see langword="null"/>.</summary>
    /// <returns>The new reference count, or 0 for <see langword="null"/>.</returns>
    public static uint Release(void* instance) =>
        instance == null ? 0 : ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(instance, SlotRelease))(instance);

    /// <summary><c>IDXGIFactory1::EnumAdapters1(UINT Adapter, IDXGIAdapter1 **ppAdapter)</c>.</summary>
    public static int EnumAdapters1(void* factory1, uint index, void** adapter) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(factory1, SlotEnumAdapters1))(factory1, index, adapter);

    /// <summary><c>IDXGIFactory6::EnumAdapterByGpuPreference(UINT Adapter, DXGI_GPU_PREFERENCE GpuPreference, REFIID riid, void **ppvAdapter)</c>.</summary>
    public static int EnumAdapterByGpuPreference(void* factory6, uint index, int preference, Guid iid, void** adapter) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int, Guid*, void**, int>)Slot(factory6, SlotEnumAdapterByGpuPreference))(factory6, index, preference, &iid, adapter);

    /// <summary><c>IDXGIAdapter::CheckInterfaceSupport(REFGUID InterfaceName, LARGE_INTEGER *pUMDVersion)</c>.</summary>
    public static int CheckInterfaceSupport(void* adapter, Guid interfaceName, long* umdVersion) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, long*, int>)Slot(adapter, SlotCheckInterfaceSupport))(adapter, &interfaceName, umdVersion);

    /// <summary><c>IDXGIAdapter1::GetDesc1(DXGI_ADAPTER_DESC1 *pDesc)</c>.</summary>
    public static int GetDesc1(void* adapter1, AdapterDesc1* desc) =>
        ((delegate* unmanaged[Stdcall]<void*, AdapterDesc1*, int>)Slot(adapter1, SlotGetDesc1))(adapter1, desc);

    /// <summary><c>IDXGIAdapter3::QueryVideoMemoryInfo(UINT NodeIndex, DXGI_MEMORY_SEGMENT_GROUP MemorySegmentGroup, DXGI_QUERY_VIDEO_MEMORY_INFO *pVideoMemoryInfo)</c>.</summary>
    public static int QueryVideoMemoryInfo(void* adapter3, uint nodeIndex, int segmentGroup, QueryVideoMemoryInfoData* info) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int, QueryVideoMemoryInfoData*, int>)Slot(adapter3, SlotQueryVideoMemoryInfo))(adapter3, nodeIndex, segmentGroup, info);

    private static void* Slot(void* instance, int slot) => (*(void***)instance)[slot];
}

/// <summary>
/// <c>DXGI_ADAPTER_DESC1</c> (<c>dxgi.h</c>): 312 bytes on x64.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdapterDesc1
{
    /// <summary><c>WCHAR Description[128]</c>.</summary>
    public AdapterDescription Description;

    /// <summary><c>UINT VendorId</c>.</summary>
    public uint VendorId;

    /// <summary><c>UINT DeviceId</c>.</summary>
    public uint DeviceId;

    /// <summary><c>UINT SubSysId</c>.</summary>
    public uint SubSysId;

    /// <summary><c>UINT Revision</c>.</summary>
    public uint Revision;

    /// <summary><c>SIZE_T DedicatedVideoMemory</c>.</summary>
    public nuint DedicatedVideoMemory;

    /// <summary><c>SIZE_T DedicatedSystemMemory</c>.</summary>
    public nuint DedicatedSystemMemory;

    /// <summary><c>SIZE_T SharedSystemMemory</c>.</summary>
    public nuint SharedSystemMemory;

    /// <summary><c>LUID.LowPart</c> (<c>DWORD</c>).</summary>
    public uint LuidLowPart;

    /// <summary><c>LUID.HighPart</c> (<c>LONG</c>).</summary>
    public int LuidHighPart;

    /// <summary><c>UINT Flags</c> (<c>DXGI_ADAPTER_FLAG</c>).</summary>
    public uint Flags;

    /// <summary>Reads <see cref="Description"/> up to the first null character.</summary>
    /// <returns>The adapter description.</returns>
    public readonly string ReadDescription()
    {
        ReadOnlySpan<char> text = Description;
        var end = text.IndexOf('\0');
        return new string(end < 0 ? text : text[..end]);
    }
}

/// <summary>The fixed 128-character <c>Description</c> buffer of <see cref="AdapterDesc1"/>.</summary>
[InlineArray(128)]
internal struct AdapterDescription
{
    private char _element0;
}

/// <summary>
/// <c>DXGI_QUERY_VIDEO_MEMORY_INFO</c> (<c>dxgi1_4.h</c>): four <c>UINT64</c>, 32 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct QueryVideoMemoryInfoData
{
    /// <summary><c>UINT64 Budget</c>.</summary>
    public ulong Budget;

    /// <summary><c>UINT64 CurrentUsage</c>.</summary>
    public ulong CurrentUsage;

    /// <summary><c>UINT64 AvailableForReservation</c>.</summary>
    public ulong AvailableForReservation;

    /// <summary><c>UINT64 CurrentReservation</c>.</summary>
    public ulong CurrentReservation;
}
