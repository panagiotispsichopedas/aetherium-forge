using System.Runtime.CompilerServices;
using AetheriumForge.Core.Gpu;
using AetheriumForge.Native;
using AetheriumForge.Native.Interop;

namespace AetheriumForge.Core.Tests.Native;

public sealed class NativeFormatTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(unchecked((int)0x887A0002), false)]
    [InlineData(unchecked((int)0x80004002), false)]
    public void Succeeded_HResult_MatchesSeverityBit(int hresult, bool expected)
    {
        // Act
        var succeeded = NativeFormat.Succeeded(hresult);

        // Assert
        Assert.Equal(expected, succeeded);
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0002), "0x887A0002 (DXGI_ERROR_NOT_FOUND)")]
    [InlineData(unchecked((int)0x80004002), "0x80004002 (E_NOINTERFACE)")]
    [InlineData(unchecked((int)0x887A0004), "0x887A0004 (DXGI_ERROR_UNSUPPORTED)")]
    [InlineData(0, "0x00000000 (S_OK)")]
    [InlineData(unchecked((int)0x80001234), "0x80001234")]
    public void FormatHResult_Code_HexWithKnownName(int hresult, string expected)
    {
        // Act
        var text = NativeFormat.FormatHResult(hresult);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void HResultToString_Value_UsesFormatHResult()
    {
        // Act
        var text = new HResult(unchecked((int)0x887A0002)).ToString();

        // Assert
        Assert.Equal("0x887A0002 (DXGI_ERROR_NOT_FOUND)", text);
    }

    [Theory]
    [InlineData(0x0000D1A5u, 0, 0x0000D1A5L)]
    [InlineData(0xFFFFFFFFu, 0, 0x00000000FFFFFFFFL)]
    [InlineData(0x00000001u, 1, 0x0000000100000001L)]
    [InlineData(0x80000000u, -1, unchecked((long)0xFFFFFFFF80000000UL))]
    public void ToLuid_Parts_CombinesHighAndLow(uint lowPart, int highPart, long expected)
    {
        // Act
        var luid = NativeFormat.ToLuid(lowPart, highPart);

        // Assert
        Assert.Equal(expected, luid);
    }

    [Theory]
    [InlineData(0x0000D1A5u, 0)]
    [InlineData(0xFFFFFFFFu, 0)]
    [InlineData(0x80000000u, -1)]
    [InlineData(0x12345678u, 0x7FFFFFFF)]
    public void SplitLuid_CombinedLuid_RoundTrips(uint lowPart, int highPart)
    {
        // Act
        var parts = NativeFormat.SplitLuid(NativeFormat.ToLuid(lowPart, highPart));

        // Assert
        Assert.Equal((lowPart, highPart), parts);
    }

    [Theory]
    [InlineData(0x001F_0000_000F_0E27L, "31.0.15.3623")]
    [InlineData(0L, "0.0.0.0")]
    [InlineData(unchecked((long)0xFFFF_FFFF_FFFF_FFFFUL), "65535.65535.65535.65535")]
    [InlineData(0x000A_0000_4A61_0001L, "10.0.19041.1")]
    public void FormatDriverVersion_UmdVersion_FourSixteenBitParts(long umdVersion, string expected)
    {
        // Act
        var text = NativeFormat.FormatDriverVersion(umdVersion);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(128UL * 1024 * 1024, 8UL * 1024 * 1024 * 1024, true)]
    [InlineData(0UL, 4UL * 1024 * 1024 * 1024, true)]
    [InlineData(512UL * 1024 * 1024, 8UL * 1024 * 1024 * 1024, false)]
    [InlineData(8UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024, false)]
    [InlineData(128UL * 1024 * 1024, 0UL, false)]
    public void LooksIntegrated_Memory_AppliesHeuristic(ulong dedicated, ulong shared, bool expected)
    {
        // Act
        var integrated = NativeFormat.LooksIntegrated(dedicated, shared);

        // Assert
        Assert.Equal(expected, integrated);
    }

    [Fact]
    public void AdapterDesc1_Layout_MatchesDxgiHeaderSize()
    {
        // Assert: 256 (WCHAR[128]) + 4×4 + 3×8 (SIZE_T) + 8 (LUID) + 4 (Flags) + 4 padding.
        Assert.Equal(312, Unsafe.SizeOf<AdapterDesc1>());
    }

    [Fact]
    public void QueryVideoMemoryInfoData_Layout_IsFourUInt64()
    {
        // Assert
        Assert.Equal(32, Unsafe.SizeOf<QueryVideoMemoryInfoData>());
    }

    [Fact]
    public void FeatureDataArchitecture_Layout_IsFourUInt32()
    {
        // Assert
        Assert.Equal(16, Unsafe.SizeOf<FeatureDataArchitecture>());
    }

    [Fact]
    public void ReadDescription_NullTerminated_StopsAtNull()
    {
        // Arrange
        var desc = default(AdapterDesc1);
        "NVIDIA GeForce GTX 1080".AsSpan().CopyTo(desc.Description);

        // Act
        var name = desc.ReadDescription();

        // Assert
        Assert.Equal("NVIDIA GeForce GTX 1080", name);
    }

    [Fact]
    public void ReadDescription_FullBuffer_ReadsAll128Characters()
    {
        // Arrange
        var desc = default(AdapterDesc1);
        new string('x', 128).AsSpan().CopyTo(desc.Description);

        // Act
        var name = desc.ReadDescription();

        // Assert
        Assert.Equal(128, name.Length);
    }

    [Fact]
    public void OrderByPreference_PreferenceList_OrdersByLuidRank()
    {
        // Arrange: EnumAdapters1 order is integrated, discrete, software; preference puts the discrete GPU first.
        GpuAdapterInfo[] adapters = [Adapter(10, 0), Adapter(20, 1), Adapter(30, 2)];

        // Act
        var ordered = DxgiAdapterProvider.OrderByPreference(adapters, [20, 10, 30]);

        // Assert
        Assert.Equal([1, 0, 2], ordered.Select(adapter => adapter.DirectMLDeviceId));
    }

    [Fact]
    public void OrderByPreference_NoPreference_KeepsEnumerationOrder()
    {
        // Arrange
        GpuAdapterInfo[] adapters = [Adapter(10, 0), Adapter(20, 1)];

        // Act
        var ordered = DxgiAdapterProvider.OrderByPreference(adapters, null);

        // Assert
        Assert.Same(adapters, ordered);
    }

    [Fact]
    public void OrderByPreference_AdapterMissingFromPreference_GoesLastInEnumerationOrder()
    {
        // Arrange
        GpuAdapterInfo[] adapters = [Adapter(10, 0), Adapter(20, 1), Adapter(30, 2)];

        // Act
        var ordered = DxgiAdapterProvider.OrderByPreference(adapters, [30]);

        // Assert
        Assert.Equal([2, 0, 1], ordered.Select(adapter => adapter.DirectMLDeviceId));
    }

    private static GpuAdapterInfo Adapter(long luid, int index) =>
        new(luid, index, $"Adapter {index}", 0x10DE, 0x1B80, 8UL << 30, 16UL << 30, IsIntegrated: false, IsSoftware: false, DriverVersion: null);
}
