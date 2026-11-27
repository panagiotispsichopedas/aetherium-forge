using System.Globalization;

namespace AetheriumForge.Native.Interop;

/// <summary>
/// Pure helpers for HRESULTs, LUIDs and driver versions.
/// </summary>
internal static class NativeFormat
{
    /// <summary>Dedicated memory below which an adapter that also has shared memory is treated as integrated by the fallback heuristic.</summary>
    public const ulong IntegratedDedicatedMemoryThreshold = 512UL * 1024 * 1024;

    /// <summary>Gets a value indicating whether an HRESULT is a success code (<c>SUCCEEDED</c>).</summary>
    /// <param name="hresult">The HRESULT.</param>
    /// <returns><see langword="true"/> when the severity bit is clear.</returns>
    public static bool Succeeded(int hresult) => hresult >= 0;

    /// <summary>Formats an HRESULT as <c>0xXXXXXXXX</c>, with its symbolic name when it is a well-known DXGI or COM code.</summary>
    /// <param name="hresult">The HRESULT.</param>
    /// <returns>For example <c>0x887A0002 (DXGI_ERROR_NOT_FOUND)</c>.</returns>
    public static string FormatHResult(int hresult)
    {
        var hex = $"0x{unchecked((uint)hresult).ToString("X8", CultureInfo.InvariantCulture)}";
        return HResultName(hresult) is { } name ? $"{hex} ({name})" : hex;
    }

    /// <summary>Combines the two halves of a Win32 <c>LUID</c> into one 64-bit value.</summary>
    /// <param name="lowPart">The <c>LowPart</c> (<c>DWORD</c>).</param>
    /// <param name="highPart">The <c>HighPart</c> (<c>LONG</c>).</param>
    /// <returns>The LUID as <c>(HighPart &lt;&lt; 32) | LowPart</c>.</returns>
    public static long ToLuid(uint lowPart, int highPart) => ((long)highPart << 32) | lowPart;

    /// <summary>Splits a 64-bit LUID back into its <c>LowPart</c> and <c>HighPart</c>.</summary>
    /// <param name="luid">The LUID.</param>
    /// <returns>The two halves.</returns>
    public static (uint LowPart, int HighPart) SplitLuid(long luid) => (unchecked((uint)luid), (int)(luid >> 32));

    /// <summary>
    /// Formats the user-mode driver version that <c>IDXGIAdapter::CheckInterfaceSupport</c> returns as four 16-bit parts, high to low.
    /// </summary>
    /// <param name="umdVersion">The <c>LARGE_INTEGER</c> version.</param>
    /// <returns>For example <c>31.0.15.3623</c>.</returns>
    public static string FormatDriverVersion(long umdVersion)
    {
        var value = unchecked((ulong)umdVersion);
        return string.Create(CultureInfo.InvariantCulture, $"{(value >> 48) & 0xFFFF}.{(value >> 32) & 0xFFFF}.{(value >> 16) & 0xFFFF}.{value & 0xFFFF}");
    }

    /// <summary>
    /// The fallback integrated-GPU heuristic, used when D3D12 cannot report <c>UMA</c>: less than 512 MB dedicated memory and some shared memory.
    /// </summary>
    /// <param name="dedicatedVideoMemory">Dedicated video memory in bytes.</param>
    /// <param name="sharedSystemMemory">Shared system memory in bytes.</param>
    /// <returns><see langword="true"/> when the adapter looks integrated.</returns>
    public static bool LooksIntegrated(ulong dedicatedVideoMemory, ulong sharedSystemMemory) =>
        dedicatedVideoMemory < IntegratedDedicatedMemoryThreshold && sharedSystemMemory > 0;

    private static string? HResultName(int hresult) => unchecked((uint)hresult) switch
    {
        0x00000000 => "S_OK",
        0x80004002 => "E_NOINTERFACE",
        0x80004005 => "E_FAIL",
        0x80070057 => "E_INVALIDARG",
        0x8007000E => "E_OUTOFMEMORY",
        0x887A0001 => "DXGI_ERROR_INVALID_CALL",
        0x887A0002 => "DXGI_ERROR_NOT_FOUND",
        0x887A0004 => "DXGI_ERROR_UNSUPPORTED",
        0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED",
        0x887A0007 => "DXGI_ERROR_DEVICE_RESET",
        0x887A0022 => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        _ => null,
    };
}

/// <summary>
/// An HRESULT for structured logging; formatted by <see cref="NativeFormat.FormatHResult"/> only when a log entry is written.
/// </summary>
/// <param name="Value">The raw HRESULT.</param>
internal readonly record struct HResult(int Value)
{
    /// <inheritdoc />
    public override string ToString() => NativeFormat.FormatHResult(Value);
}
