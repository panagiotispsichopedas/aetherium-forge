using AetheriumForge.Core.Imaging;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Tests.Imaging;

/// <summary>
/// Texconv tests that do not need the real executable: argument building and process handling against a fake <c>.cmd</c> script.
/// </summary>
public sealed class TexconvTests
{
    // A fake texconv: fails unless -nogpu is passed; otherwise copies the input file into the -o folder.
    private const string FakeGpuFailingScript = """
        @echo off
        set "nogpu="
        set "out="
        set "in="
        :loop
        if "%~1"=="" goto done
        if "%~1"=="-nogpu" set "nogpu=1"
        if "%~1"=="-o" (
          set "out=%~2"
          shift
        )
        set "in=%~1"
        shift
        goto loop
        :done
        if not defined nogpu (
          echo writing texture.dds FAILED fake GPU failure
          exit /b 1
        )
        copy /y "%in%" "%out%\" >nul
        echo writing ok
        exit /b 0
        """;

    private const string FakeSlowScript = """
        @echo off
        echo started
        ping -n 30 127.0.0.1 >nul
        exit /b 0
        """;

    private const string FakeVersionScript = """
        @echo off
        echo texconv version 2026.5.8.1
        exit /b 0
        """;

    [Theory]
    [InlineData(false, "R8G8B8A8_UNORM")]
    [InlineData(true, "R8G8B8A8_UNORM_SRGB")]
    public void BuildDecodeArguments_SrgbFlag_SelectsMatchingUncompressedFormat(bool srgb, string format)
    {
        // Act
        IReadOnlyList<string> arguments = Texconv.BuildDecodeArguments(@"C:\in dir\a.dds", @"C:\out dir", srgb);

        // Assert
        Assert.Equal(["-nologo", "-y", "-ft", "dds", "-f", format, "-dx10", "-m", "1", "-tu", "-o", @"C:\out dir", @"C:\in dir\a.dds"], arguments);
    }

    [Fact]
    public void BuildEncodeArguments_GpuDefaultAdapter_HasNoGpuSwitches()
    {
        // Act
        IReadOnlyList<string> arguments = Texconv.BuildEncodeArguments(@"C:\w\texture.dds", @"C:\w\out", srgb: false, useGpu: true, gpuAdapter: null);

        // Assert
        Assert.Equal(["-nologo", "-y", "-f", "BC7_UNORM", "-m", "0", "-dx10", "-o", @"C:\w\out", @"C:\w\texture.dds"], arguments);
    }

    [Fact]
    public void BuildEncodeArguments_GpuAdapter_AddsGpuIndex()
    {
        // Act
        IReadOnlyList<string> arguments = Texconv.BuildEncodeArguments("in.dds", "out", srgb: false, useGpu: true, gpuAdapter: 2);

        // Assert
        Assert.Equal(["-nologo", "-y", "-f", "BC7_UNORM", "-m", "0", "-dx10", "-gpu", "2", "-o", "out", "in.dds"], arguments);
    }

    [Fact]
    public void BuildEncodeArguments_NoGpu_AddsNoGpuAndIgnoresAdapter()
    {
        // Act
        IReadOnlyList<string> arguments = Texconv.BuildEncodeArguments("in.dds", "out", srgb: true, useGpu: false, gpuAdapter: 1);

        // Assert
        Assert.Equal(["-nologo", "-y", "-f", "BC7_UNORM_SRGB", "-m", "0", "-dx10", "-nogpu", "-o", "out", "in.dds"], arguments);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(28, false)]
    [InlineData(29, true)]
    [InlineData(71, false)]
    [InlineData(72, true)]
    [InlineData(98, false)]
    [InlineData(99, true)]
    public void IsSrgbFormat_DxgiFormat_DetectsSrgbVariants(int? dxgi, bool expected)
    {
        // Act
        bool srgb = Texconv.IsSrgbFormat(dxgi);

        // Assert
        Assert.Equal(expected, srgb);
    }

    [Theory]
    [InlineData("writing x.dds FAILED (80070003)", true)]
    [InlineData("ERROR: Invalid GPU adapter index (99)!\nwriting x.dds", false)]
    [InlineData("writing x.dds", false)]
    public void ReportsFailure_Output_DetectsFailedMarker(string output, bool expected)
    {
        // Act
        bool failed = Texconv.ReportsFailure(output);

        // Assert
        Assert.Equal(expected, failed);
    }

    [Fact]
    public void Tail_LongOutput_KeepsEnd()
    {
        // Arrange
        string output = new string('a', 5000) + "END";

        // Act
        string tail = Texconv.Tail(output);

        // Assert
        Assert.Equal((4000, true), (tail.Length, tail.EndsWith("END", StringComparison.Ordinal)));
    }

    [Fact]
    public void Constructor_EmptyPath_ThrowsArgumentException()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => new Texconv(new TexconvOptions { ExecutablePath = " " }));
    }

    [Fact]
    public async Task GetVersionAsync_MissingExecutable_ThrowsTexconvException()
    {
        // Arrange
        using var folder = new TempFolder();
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = Path.Combine(folder.Path, "missing.exe") });

        // Act + Assert
        await Assert.ThrowsAsync<TexconvException>(() => texconv.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetVersionAsync_VersionOutput_ParsesVersion()
    {
        // Arrange
        using var folder = new TempFolder();
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = WriteScript(folder, "version.cmd", FakeVersionScript) });

        // Act
        string version = await texconv.GetVersionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("2026.5.8.1", version);
    }

    [Fact]
    public async Task EncodeBc7Async_GpuRunFails_RetriesWithNoGpuAndLogsWarning()
    {
        // Arrange
        using var folder = new TempFolder();
        var logger = new ListLogger<Texconv>();
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = WriteScript(folder, "fake.cmd", FakeGpuFailingScript) }, logger);
        string output = Path.Combine(folder.Path, "final", "sub", "result.dds");
        RgbaImage image = ImagingTestData.Noise(4, 4);

        // Act
        await texconv.EncodeBc7Async(image, output, Path.Combine(folder.Path, "work"), TestContext.Current.CancellationToken);

        // Assert: the fake copies its input, so the final file is the raw intermediate DDS.
        Assert.Equal(RawDds.Write(image), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("-nogpu", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(folder.Path, "work")));
    }

    [Fact]
    public async Task EncodeBc7Async_GpuDisabledAndRunFails_ThrowsTexconvExceptionWithOutput()
    {
        // Arrange: with UseGpu off the fake succeeds; force failure by making the fake fail on every run.
        using var folder = new TempFolder();
        string script = WriteScript(folder, "fail.cmd", "@echo off\r\necho reading x FAILED boom\r\nexit /b 3\r\n");
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = script, UseGpu = false });

        // Act
        TexconvException ex = await Assert.ThrowsAsync<TexconvException>(
            () => texconv.EncodeBc7Async(ImagingTestData.Noise(2, 2), Path.Combine(folder.Path, "o.dds"), folder.Path, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((3, true), (ex.ExitCode, ex.OutputTail.Contains("boom", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DecodeAsync_ProcessExceedsTimeout_KillsAndThrowsTimedOut()
    {
        // Arrange
        using var folder = new TempFolder();
        string source = Path.Combine(folder.Path, "a.dds");
        await RawDds.WriteAsync(ImagingTestData.Noise(2, 2), source, TestContext.Current.CancellationToken);
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = WriteScript(folder, "slow.cmd", FakeSlowScript), Timeout = TimeSpan.FromSeconds(1) });
        var started = DateTime.UtcNow;

        // Act
        TexconvException ex = await Assert.ThrowsAsync<TexconvException>(() => texconv.DecodeAsync(source, folder.Path, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(ex.TimedOut && ex.ExitCode is null && DateTime.UtcNow - started < TimeSpan.FromSeconds(20), $"timedOut={ex.TimedOut}, exit={ex.ExitCode}");
    }

    [Fact]
    public async Task DecodeAsync_Cancelled_KillsAndThrowsOperationCanceled()
    {
        // Arrange
        using var folder = new TempFolder();
        string source = Path.Combine(folder.Path, "a.dds");
        await RawDds.WriteAsync(ImagingTestData.Noise(2, 2), source, TestContext.Current.CancellationToken);
        var texconv = new Texconv(new TexconvOptions { ExecutablePath = WriteScript(folder, "slow.cmd", FakeSlowScript) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => texconv.DecodeAsync(source, Path.Combine(folder.Path, "work"), cts.Token));

        // Assert
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    private static string WriteScript(TempFolder folder, string name, string content)
    {
        string path = Path.Combine(folder.Path, name);
        File.WriteAllText(path, content.ReplaceLineEndings("\r\n"));
        return path;
    }
}
