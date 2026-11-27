using System.Collections.Concurrent;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Native.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Native;

/// <summary>
/// Windows implementation of <see cref="IGpuAdapterProvider"/> on DXGI (IMPLEMENTATION_GUIDE §9, "GPU auto profile").
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="GpuAdapterInfo.DirectMLDeviceId"/> is the <c>IDXGIFactory1::EnumAdapters1</c> index, which ONNX Runtime's
/// DirectML provider uses as its <c>deviceId</c>. Adapters are matched between enumerations by LUID. The index is a snapshot: it can
/// change when an adapter is attached, detached or reordered (eGPU, driver reinstall, Basic Render Driver fallback). Consumers must
/// not persist it; persist the adapter identity instead and re-resolve the index from a fresh <see cref="GetAdapters"/> call right
/// before creating an ONNX Runtime session.</item>
/// <item>Order is <c>IDXGIFactory6::EnumAdapterByGpuPreference(HIGH_PERFORMANCE)</c>; without <c>IDXGIFactory6</c> it is the
/// <c>EnumAdapters1</c> order.</item>
/// <item><see cref="GpuAdapterInfo.IsIntegrated"/> comes from D3D12 <c>D3D12_FEATURE_DATA_ARCHITECTURE.UMA</c>, cached per LUID
/// because creating a device is expensive. When D3D12 is unavailable the heuristic "dedicated memory &lt; 512 MB and shared
/// memory &gt; 0" applies for that call only; a failed probe is not cached and is retried on the next call. Software adapters are
/// never reported as integrated.</item>
/// <item>A new DXGI factory is created per call, so adapter changes (driver update, eGPU) are picked up. Every COM reference is
/// released before the call returns.</item>
/// <item>Nothing throws when DXGI is missing or fails: <see cref="GetAdapters"/> returns an empty list and
/// <see cref="QueryVideoMemory"/> returns <see langword="null"/>; failures are logged with their HRESULT.</item>
/// </list>
/// </remarks>
public sealed unsafe partial class DxgiAdapterProvider : IGpuAdapterProvider
{
    /// <summary>The GPU node <see cref="QueryVideoMemory"/> reads: node 0, the one ONNX Runtime's DirectML provider runs on.</summary>
    internal const uint VideoMemoryNodeIndex = 0;

    private readonly ILogger<DxgiAdapterProvider> _logger;
    private readonly ConcurrentDictionary<long, bool> _integratedByLuid = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DxgiAdapterProvider"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for DXGI failures.</param>
    public DxgiAdapterProvider(ILogger<DxgiAdapterProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<DxgiAdapterProvider>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<GpuAdapterInfo> GetAdapters()
    {
        var factory = CreateFactory();
        if (factory == null)
        {
            return [];
        }

        try
        {
            var adapters = EnumerateAdapters(factory);
            var preference = adapters.Count > 0 ? EnumeratePreferenceOrder(factory) : null;
            return OrderByPreference(adapters, preference);
        }
        finally
        {
            DxgiNative.Release(factory);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads node 0 only (<see cref="VideoMemoryNodeIndex"/>), so the result covers single-node adapters, which is every adapter
    /// apart from linked-display-adapter (SLI/CrossFire LDA) setups. This is deliberate: ONNX Runtime's DirectML provider creates
    /// its device and queues on node 0, so node 0's local budget is the memory a session can use. Summing nodes would overstate it.
    /// </remarks>
    public VideoMemoryInfo? QueryVideoMemory(long luid)
    {
        var factory = CreateFactory();
        if (factory == null)
        {
            return null;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                void* adapter = null;
                var hr = DxgiNative.EnumAdapters1(factory, index, &adapter);
                if (hr == DxgiNative.ErrorNotFound)
                {
                    LogAdapterNotFound(luid);
                    return null;
                }

                if (!NativeFormat.Succeeded(hr))
                {
                    LogCallFailed("IDXGIFactory1::EnumAdapters1", index, new HResult(hr));
                    return null;
                }

                try
                {
                    AdapterDesc1 desc;
                    hr = DxgiNative.GetDesc1(adapter, &desc);
                    if (!NativeFormat.Succeeded(hr))
                    {
                        LogCallFailed("IDXGIAdapter1::GetDesc1", index, new HResult(hr));
                        continue;
                    }

                    if (NativeFormat.ToLuid(desc.LuidLowPart, desc.LuidHighPart) == luid)
                    {
                        return ReadVideoMemory(adapter, index);
                    }
                }
                finally
                {
                    DxgiNative.Release(adapter);
                }
            }
        }
        finally
        {
            DxgiNative.Release(factory);
        }
    }

    /// <summary>
    /// Orders adapters by a preference list of LUIDs; adapters missing from it keep their relative order at the end.
    /// </summary>
    /// <param name="adapters">The adapters in <c>EnumAdapters1</c> order.</param>
    /// <param name="preferredLuids">LUIDs in preference order, or <see langword="null"/> to keep <paramref name="adapters"/> as is.</param>
    /// <returns>The ordered adapters.</returns>
    internal static IReadOnlyList<GpuAdapterInfo> OrderByPreference(IReadOnlyList<GpuAdapterInfo> adapters, IReadOnlyList<long>? preferredLuids)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (preferredLuids is null)
        {
            return adapters;
        }

        var rank = new Dictionary<long, int>();
        for (var i = 0; i < preferredLuids.Count; i++)
        {
            rank.TryAdd(preferredLuids[i], i);
        }

        return [.. adapters
            .Select((adapter, index) => (Adapter: adapter, Index: index))
            .OrderBy(item => rank.TryGetValue(item.Adapter.Luid, out var position) ? position : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Adapter)];
    }

    private void* CreateFactory()
    {
        void* factory = null;
        var iid = DxgiNative.IidFactory1;
        int hr;
        try
        {
            try
            {
                hr = DxgiNative.CreateDXGIFactory2(0, &iid, &factory);
            }
            catch (EntryPointNotFoundException exception)
            {
                // Windows 7/8 without CreateDXGIFactory2: fall back to CreateDXGIFactory1.
                LogFactory2Missing(exception);
                hr = DxgiNative.CreateDXGIFactory1(&iid, &factory);
            }
        }
        catch (DllNotFoundException exception)
        {
            LogDxgiUnavailable(exception);
            return null;
        }
        catch (EntryPointNotFoundException exception)
        {
            LogDxgiUnavailable(exception);
            return null;
        }

        if (!NativeFormat.Succeeded(hr) || factory == null)
        {
            LogFactoryFailed(new HResult(hr));
            DxgiNative.Release(factory);
            return null;
        }

        return factory;
    }

    private List<GpuAdapterInfo> EnumerateAdapters(void* factory)
    {
        var adapters = new List<GpuAdapterInfo>();
        for (uint index = 0; ; index++)
        {
            void* adapter = null;
            var hr = DxgiNative.EnumAdapters1(factory, index, &adapter);
            if (hr == DxgiNative.ErrorNotFound)
            {
                return adapters;
            }

            if (!NativeFormat.Succeeded(hr))
            {
                LogCallFailed("IDXGIFactory1::EnumAdapters1", index, new HResult(hr));
                return adapters;
            }

            try
            {
                if (Describe(adapter, index) is { } info)
                {
                    adapters.Add(info);
                }
            }
            finally
            {
                DxgiNative.Release(adapter);
            }
        }
    }

    private GpuAdapterInfo? Describe(void* adapter, uint index)
    {
        AdapterDesc1 desc;
        var hr = DxgiNative.GetDesc1(adapter, &desc);
        if (!NativeFormat.Succeeded(hr))
        {
            LogCallFailed("IDXGIAdapter1::GetDesc1", index, new HResult(hr));
            return null;
        }

        var name = desc.ReadDescription();
        var luid = NativeFormat.ToLuid(desc.LuidLowPart, desc.LuidHighPart);
        var dedicated = (ulong)desc.DedicatedVideoMemory;
        var shared = (ulong)desc.SharedSystemMemory;
        var isSoftware = (desc.Flags & DxgiNative.AdapterFlagSoftware) != 0;
        var isIntegrated = !isSoftware && IsIntegrated(adapter, name, luid, dedicated, shared);

        string? driverVersion = null;
        long umdVersion;
        hr = DxgiNative.CheckInterfaceSupport(adapter, DxgiNative.IidDevice, &umdVersion);
        if (NativeFormat.Succeeded(hr))
        {
            driverVersion = NativeFormat.FormatDriverVersion(umdVersion);
        }
        else
        {
            LogDriverVersionUnavailable(name, new HResult(hr));
        }

        LogAdapterFound(index, name, luid, dedicated, isSoftware, isIntegrated, driverVersion);
        return new GpuAdapterInfo(luid, (int)index, name, (int)desc.VendorId, (int)desc.DeviceId, dedicated, shared, isIntegrated, isSoftware, driverVersion);
    }

    private List<long>? EnumeratePreferenceOrder(void* factory)
    {
        void* factory6 = null;
        var hr = DxgiNative.QueryInterface(factory, DxgiNative.IidFactory6, &factory6);
        if (!NativeFormat.Succeeded(hr) || factory6 == null)
        {
            LogFactory6Unavailable(new HResult(hr));
            return null;
        }

        try
        {
            var luids = new List<long>();
            for (uint index = 0; ; index++)
            {
                void* adapter = null;
                hr = DxgiNative.EnumAdapterByGpuPreference(factory6, index, DxgiNative.GpuPreferenceHighPerformance, DxgiNative.IidAdapter1, &adapter);
                if (hr == DxgiNative.ErrorNotFound)
                {
                    return luids;
                }

                if (!NativeFormat.Succeeded(hr))
                {
                    LogCallFailed("IDXGIFactory6::EnumAdapterByGpuPreference", index, new HResult(hr));
                    return luids.Count > 0 ? luids : null;
                }

                try
                {
                    AdapterDesc1 desc;
                    hr = DxgiNative.GetDesc1(adapter, &desc);
                    if (NativeFormat.Succeeded(hr))
                    {
                        luids.Add(NativeFormat.ToLuid(desc.LuidLowPart, desc.LuidHighPart));
                    }
                    else
                    {
                        LogCallFailed("IDXGIAdapter1::GetDesc1", index, new HResult(hr));
                    }
                }
                finally
                {
                    DxgiNative.Release(adapter);
                }
            }
        }
        finally
        {
            DxgiNative.Release(factory6);
        }
    }

    private VideoMemoryInfo? ReadVideoMemory(void* adapter, uint index)
    {
        void* adapter3 = null;
        var hr = DxgiNative.QueryInterface(adapter, DxgiNative.IidAdapter3, &adapter3);
        if (!NativeFormat.Succeeded(hr) || adapter3 == null)
        {
            LogCallFailed("QueryInterface(IDXGIAdapter3)", index, new HResult(hr));
            return null;
        }

        try
        {
            QueryVideoMemoryInfoData info;
            hr = DxgiNative.QueryVideoMemoryInfo(adapter3, VideoMemoryNodeIndex, DxgiNative.MemorySegmentGroupLocal, &info);
            if (!NativeFormat.Succeeded(hr))
            {
                LogCallFailed("IDXGIAdapter3::QueryVideoMemoryInfo", index, new HResult(hr));
                return null;
            }

            return new VideoMemoryInfo(info.Budget, info.CurrentUsage, info.AvailableForReservation, info.CurrentReservation);
        }
        finally
        {
            DxgiNative.Release(adapter3);
        }
    }

    /// <summary>
    /// Resolves whether an adapter is integrated. Only a definitive D3D12 <c>UMA</c> answer is cached; when the probe fails
    /// (D3D12 missing, device creation failed, device removed) the memory heuristic answers this call and the next call probes again.
    /// </summary>
    /// <param name="cache">Definitive answers by LUID.</param>
    /// <param name="luid">The adapter LUID.</param>
    /// <param name="dedicated">Dedicated video memory in bytes, for the heuristic.</param>
    /// <param name="shared">Shared system memory in bytes, for the heuristic.</param>
    /// <param name="probe">The D3D12 <c>UMA</c> probe; <see langword="null"/> means it could not tell.</param>
    /// <returns><see langword="true"/> when the adapter is integrated.</returns>
    internal static bool ResolveIntegrated(ConcurrentDictionary<long, bool> cache, long luid, ulong dedicated, ulong shared, Func<bool?> probe)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(probe);
        if (cache.TryGetValue(luid, out var cached))
        {
            return cached;
        }

        if (probe() is bool uma)
        {
            cache[luid] = uma;
            return uma;
        }

        return NativeFormat.LooksIntegrated(dedicated, shared);
    }

    private bool IsIntegrated(void* adapter, string name, long luid, ulong dedicated, ulong shared)
    {
        if (_integratedByLuid.TryGetValue(luid, out var cached))
        {
            return cached;
        }

        var handle = (nint)adapter;
        return ResolveIntegrated(_integratedByLuid, luid, dedicated, shared, () => QueryUma((void*)handle, name));
    }

    private bool? QueryUma(void* adapter, string name)
    {
        void* device = null;
        var iid = D3D12Native.IidDevice;
        int hr;
        try
        {
            hr = D3D12Native.D3D12CreateDevice(adapter, D3D12Native.FeatureLevel110, &iid, &device);
        }
        catch (DllNotFoundException exception)
        {
            LogD3D12Unavailable(exception);
            return null;
        }
        catch (EntryPointNotFoundException exception)
        {
            LogD3D12Unavailable(exception);
            return null;
        }

        if (!NativeFormat.Succeeded(hr) || device == null)
        {
            LogUmaFallback(name, "D3D12CreateDevice", new HResult(hr));
            DxgiNative.Release(device);
            return null;
        }

        try
        {
            var architecture = default(FeatureDataArchitecture);
            hr = D3D12Native.CheckFeatureSupport(device, D3D12Native.FeatureArchitecture, &architecture, (uint)sizeof(FeatureDataArchitecture));
            if (!NativeFormat.Succeeded(hr))
            {
                LogUmaFallback(name, "ID3D12Device::CheckFeatureSupport(ARCHITECTURE)", new HResult(hr));
                return null;
            }

            return architecture.Uma != 0;
        }
        finally
        {
            DxgiNative.Release(device);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DXGI is not available; no GPU adapters can be enumerated")]
    private partial void LogDxgiUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CreateDXGIFactory2 is not available; falling back to CreateDXGIFactory1")]
    private partial void LogFactory2Missing(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating the DXGI factory failed with HRESULT {HResult}")]
    private partial void LogFactoryFailed(HResult hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Call} failed for adapter {Index} with HRESULT {HResult}")]
    private partial void LogCallFailed(string call, uint index, HResult hResult);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IDXGIFactory6 is not available (HRESULT {HResult}); using EnumAdapters1 order")]
    private partial void LogFactory6Unavailable(HResult hResult);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No user-mode driver version for {Adapter} (HRESULT {HResult})")]
    private partial void LogDriverVersionUnavailable(string adapter, HResult hResult);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Direct3D 12 is not available; integrated GPUs are detected by the memory heuristic")]
    private partial void LogD3D12Unavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Call} failed for {Adapter} with HRESULT {HResult}; integrated GPU detected by the memory heuristic")]
    private partial void LogUmaFallback(string adapter, string call, HResult hResult);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No DXGI adapter has LUID {Luid:X16}")]
    private partial void LogAdapterNotFound(long luid);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "DXGI adapter {Index}: {Adapter} (LUID {Luid:X16}, {DedicatedBytes} bytes dedicated, software {IsSoftware}, integrated {IsIntegrated}, driver {DriverVersion})")]
    private partial void LogAdapterFound(uint index, string adapter, long luid, ulong dedicatedBytes, bool isSoftware, bool isIntegrated, string? driverVersion);
}
