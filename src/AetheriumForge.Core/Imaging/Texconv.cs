using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AetheriumForge.Core.Textures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Runs Microsoft's <c>texconv.exe</c> to decode source DDS files to uncompressed RGBA and to encode RGBA to BC7 with a full
/// mip chain (IMPLEMENTATION_GUIDE §10; uncompressed DDS replaces PNG as the interchange format).
/// </summary>
/// <remarks>
/// <para>
/// texconv applies a gamma conversion whenever the sRGB-ness of the input and output formats differ. To keep pixel values
/// unchanged, decoding picks <c>R8G8B8A8_UNORM_SRGB</c> for sRGB sources and <c>R8G8B8A8_UNORM</c> otherwise, and encoding
/// tags the intermediate file with the same sRGB-ness as the BC7 target. Neither <c>-srgbi</c> nor <c>-srgbo</c> is passed.
/// </para>
/// <para>Each call works in its own unique subfolder of the given work folder, so calls may run concurrently.</para>
/// </remarks>
public sealed partial class Texconv : ITextureCodec
{
    private const int OutputTailLength = 4000;
    private const string DecodeFormat = "R8G8B8A8_UNORM";
    private const string DecodeFormatSrgb = "R8G8B8A8_UNORM_SRGB";
    private const string EncodeFormat = "BC7_UNORM";
    private const string EncodeFormatSrgb = "BC7_UNORM_SRGB";
    private const string EncodeInputName = "texture.dds";

    private readonly TexconvOptions _options;
    private readonly ILogger<Texconv> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Texconv"/> class.
    /// </summary>
    /// <param name="options">The texconv settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="ArgumentException">The executable path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout or GPU adapter is invalid.</exception>
    public Texconv(TexconvOptions options, ILogger<Texconv>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Timeout, TimeSpan.Zero, nameof(options));
        if (options.GpuAdapter is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.GpuAdapter, "The GPU adapter index must not be negative.");
        }

        _options = options;
        _logger = logger ?? NullLogger<Texconv>.Instance;
    }

    /// <summary>
    /// Gets a value indicating whether a DXGI format stores sRGB-encoded colour (for example BC1_UNORM_SRGB or BC7_UNORM_SRGB).
    /// </summary>
    /// <param name="dxgiFormat">The DXGI format from a DX10 header, or <see langword="null"/> for a legacy header (never sRGB).</param>
    /// <returns><see langword="true"/> for the sRGB variants of R8G8B8A8, BC1, BC2, BC3, B8G8R8A8, B8G8R8X8 and BC7.</returns>
    public static bool IsSrgbFormat(int? dxgiFormat) => dxgiFormat is 29 or 72 or 75 or 78 or 91 or 93 or 99;

    /// <summary>
    /// Decodes the top mip level of a DDS file of any format texconv understands to RGBA8 without changing the stored values.
    /// </summary>
    /// <param name="ddsPath">The source DDS file.</param>
    /// <param name="workFolder">A folder for temporary files; created when missing.</param>
    /// <param name="ct">Cancels the run and kills texconv.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="TexconvException">texconv failed or timed out.</exception>
    /// <exception cref="InvalidDataException">texconv produced an unexpected file.</exception>
    public async Task<RgbaImage> DecodeAsync(string ddsPath, string workFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ddsPath);
        ArgumentException.ThrowIfNullOrEmpty(workFolder);

        string source = Path.GetFullPath(ddsPath);
        bool srgb = await IsSrgbFileAsync(source, ct).ConfigureAwait(false);
        string tempFolder = CreateTempFolder(workFolder, "decode");
        try
        {
            await RunAsync(BuildDecodeArguments(source, tempFolder, srgb), ct).ConfigureAwait(false);
            string decoded = Path.Combine(tempFolder, $"{Path.GetFileNameWithoutExtension(source)}.dds");
            if (!File.Exists(decoded))
            {
                throw new TexconvException($"texconv did not produce '{decoded}'.", 0, string.Empty);
            }

            return await RawDds.ReadAsync(decoded, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteTempFolder(tempFolder);
        }
    }

    /// <summary>
    /// Encodes an image to a BC7_UNORM DDS with a full mip chain.
    /// </summary>
    /// <param name="image">The image to encode.</param>
    /// <param name="outputDdsPath">The final DDS path; parent directories are created and an existing file is replaced.</param>
    /// <param name="workFolder">A folder for temporary files; created when missing.</param>
    /// <param name="ct">Cancels the run and kills texconv.</param>
    /// <returns>A task that completes when <paramref name="outputDdsPath"/> holds the encoded texture.</returns>
    /// <exception cref="TexconvException">texconv failed (also after the CPU retry) or timed out.</exception>
    public Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, CancellationToken ct = default) =>
        EncodeBc7Async(image, outputDdsPath, workFolder, srgb: false, ct);

    /// <summary>
    /// Encodes an image to a BC7 DDS with a full mip chain, as BC7_UNORM or BC7_UNORM_SRGB.
    /// </summary>
    /// <remarks>
    /// The GPU encoder is tried first (unless <see cref="TexconvOptions.UseGpu"/> is off); on failure the encode is retried once
    /// with <c>-nogpu</c>. The result is written inside <paramref name="workFolder"/> and then moved to <paramref name="outputDdsPath"/>.
    /// </remarks>
    /// <param name="image">The image to encode.</param>
    /// <param name="outputDdsPath">The final DDS path; parent directories are created and an existing file is replaced.</param>
    /// <param name="workFolder">A folder for temporary files; created when missing.</param>
    /// <param name="srgb">
    /// <see langword="true"/> to encode BC7_UNORM_SRGB (use this when the source was sRGB, see <see cref="IsSrgbFormat"/>).
    /// The pixel bytes are stored unchanged either way.
    /// </param>
    /// <param name="ct">Cancels the run and kills texconv.</param>
    /// <returns>A task that completes when <paramref name="outputDdsPath"/> holds the encoded texture.</returns>
    /// <exception cref="TexconvException">texconv failed (also after the CPU retry) or timed out.</exception>
    public async Task EncodeBc7Async(RgbaImage image, string outputDdsPath, string workFolder, bool srgb, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(outputDdsPath);
        ArgumentException.ThrowIfNullOrEmpty(workFolder);

        string target = Path.GetFullPath(outputDdsPath);
        string tempFolder = CreateTempFolder(workFolder, "encode");
        try
        {
            string input = Path.Combine(tempFolder, EncodeInputName);
            string outputFolder = Path.Combine(tempFolder, "out");
            Directory.CreateDirectory(outputFolder);
            await RawDds.WriteAsync(image, input, srgb, ct).ConfigureAwait(false);

            string encoded = Path.Combine(outputFolder, EncodeInputName);
            if (_options.UseGpu)
            {
                try
                {
                    string output = await EncodeOnceAsync(input, outputFolder, encoded, srgb, useGpu: true, ct).ConfigureAwait(false);
                    if (output.Contains("DirectCompute is not available", StringComparison.Ordinal))
                    {
                        string tail = Tail(output);
                        LogGpuUnavailable(target, tail);
                    }
                }
                catch (TexconvException ex) when (!ct.IsCancellationRequested)
                {
                    LogGpuEncodeFailed(target, ex.ExitCode, ex.OutputTail, ex);
                    await EncodeOnceAsync(input, outputFolder, encoded, srgb, useGpu: false, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await EncodeOnceAsync(input, outputFolder, encoded, srgb, useGpu: false, ct).ConfigureAwait(false);
            }

            string? targetFolder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
            }

            File.Move(encoded, target, overwrite: true);
        }
        finally
        {
            DeleteTempFolder(tempFolder);
        }
    }

    /// <summary>
    /// Gets the texconv version, for example <c>2026.5.8.1</c>.
    /// </summary>
    /// <param name="ct">Cancels the run and kills texconv.</param>
    /// <returns>The version string.</returns>
    /// <exception cref="TexconvException">texconv could not be run or printed no version.</exception>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        string output = await RunAsync(["--version"], ct).ConfigureAwait(false);
        Match match = VersionPattern().Match(output);
        if (!match.Success)
        {
            throw new TexconvException("texconv did not report a version.", 0, Tail(output));
        }

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Builds the texconv arguments that decode the top mip of <paramref name="input"/> to an uncompressed DX10 DDS.
    /// </summary>
    /// <param name="input">The full source path.</param>
    /// <param name="outputFolder">The full output folder.</param>
    /// <param name="srgb">Whether the source format is sRGB; the output format then matches so no gamma conversion is applied.</param>
    /// <returns>The argument list.</returns>
    internal static IReadOnlyList<string> BuildDecodeArguments(string input, string outputFolder, bool srgb) =>
    [
        "-nologo",
        "-y",
        "-ft", "dds",
        "-f", srgb ? DecodeFormatSrgb : DecodeFormat,
        "-dx10",
        "-m", "1",
        "-tu",
        "-o", outputFolder,
        input,
    ];

    /// <summary>
    /// Builds the texconv arguments that encode <paramref name="input"/> to BC7 with a full mip chain.
    /// </summary>
    /// <param name="input">The full path of the uncompressed intermediate DDS.</param>
    /// <param name="outputFolder">The full output folder.</param>
    /// <param name="srgb">Whether to encode BC7_UNORM_SRGB; must match the intermediate file's tag.</param>
    /// <param name="useGpu">Whether the DirectCompute encoder may be used.</param>
    /// <param name="gpuAdapter">The GPU adapter, or <see langword="null"/> for the default; ignored when <paramref name="useGpu"/> is off.</param>
    /// <returns>The argument list.</returns>
    internal static IReadOnlyList<string> BuildEncodeArguments(string input, string outputFolder, bool srgb, bool useGpu, int? gpuAdapter)
    {
        var arguments = new List<string>(14)
        {
            "-nologo",
            "-y",
            "-f", srgb ? EncodeFormatSrgb : EncodeFormat,
            "-m", "0",
            "-dx10",
        };

        if (!useGpu)
        {
            arguments.Add("-nogpu");
        }
        else if (gpuAdapter is int adapter)
        {
            arguments.Add("-gpu");
            arguments.Add(adapter.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-o");
        arguments.Add(outputFolder);
        arguments.Add(input);
        return arguments;
    }

    /// <summary>
    /// Returns the last <c>OutputTailLength</c> characters of <paramref name="output"/>.
    /// </summary>
    /// <param name="output">The process output.</param>
    /// <returns>The tail, trimmed.</returns>
    internal static string Tail(string output)
    {
        string trimmed = output.Trim();
        return trimmed.Length <= OutputTailLength ? trimmed : trimmed[^OutputTailLength..];
    }

    /// <summary>
    /// Gets a value indicating whether texconv reported a failure in its output even though it may have exited with code 0.
    /// </summary>
    /// <param name="output">The process output.</param>
    /// <remarks>
    /// texconv reports a failed file on its status line as <c>reading|writing &lt;path&gt; FAILED (80004005: message )</c>, or with a
    /// stage tag such as <c>FAILED [resize] (...)</c>. Only that status token counts: texconv echoes paths, so a folder named
    /// <c>FAILED</c> (echoed as <c>\FAILED\</c>) must not fail the run. <c>ERROR:</c> lines alone (for example an invalid
    /// <c>-gpu</c> index, after which texconv falls back to the CPU codec) are not failures.
    /// </remarks>
    /// <returns><see langword="true"/> when a line carries the <c>FAILED (hresult</c> status token.</returns>
    internal static bool ReportsFailure(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (var line in output.AsSpan().EnumerateLines())
        {
            if (FailureStatusPattern().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> EncodeOnceAsync(string input, string outputFolder, string encoded, bool srgb, bool useGpu, CancellationToken ct)
    {
        string output = await RunAsync(BuildEncodeArguments(input, outputFolder, srgb, useGpu, useGpu ? _options.GpuAdapter : null), ct).ConfigureAwait(false);
        if (!File.Exists(encoded))
        {
            throw new TexconvException($"texconv did not produce '{encoded}'.", 0, Tail(output));
        }

        return output;
    }

    private static async Task<bool> IsSrgbFileAsync(string path, CancellationToken ct)
    {
        byte[] buffer = new byte[DdsHeader.MinimumLength];
        int read;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        return DdsHeader.TryParse(buffer.AsSpan(0, read), out DdsHeader header) && IsSrgbFormat(header.DxgiFormat);
    }

    private static string CreateTempFolder(string workFolder, string prefix)
    {
        string folder = Path.Combine(Path.GetFullPath(workFolder), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [GeneratedRegex(@"[Vv]ersion\s+(\d+(?:\.\d+)+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    // A whitespace-delimited FAILED, an optional [stage] tag, then "(" and the eight-digit hexadecimal HRESULT.
    [GeneratedRegex(@"(?:^|\s)FAILED(?:\s+\[[^\]]*\])?\s+\([0-9A-Fa-f]{8}[:)]", RegexOptions.CultureInvariant)]
    private static partial Regex FailureStatusPattern();

    private void DeleteTempFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTempCleanupFailed(folder, ex);
        }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(_options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string commandLine = string.Join(' ', arguments);
        LogRunning(_options.ExecutablePath, commandLine);
        long started = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new TexconvException($"Could not start texconv at '{_options.ExecutablePath}': {ex.Message}", ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string partial = Combine(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();
            throw new TexconvException($"texconv did not finish within {_options.Timeout} and was killed.", null, Tail(partial), timedOut: true);
        }

        string output = Combine(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        int exitCode = process.ExitCode;
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogFinished(exitCode, elapsedMs);

        if (exitCode != 0 || ReportsFailure(output))
        {
            throw new TexconvException($"texconv failed with exit code {exitCode}: {LastLine(output)}", exitCode, Tail(output));
        }

        return output;
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            // The process exited between the cancellation and the kill; nothing is left to stop.
            LogKillSkipped(ex.Message);
        }
        catch (Win32Exception ex)
        {
            LogKillFailed(ex);
            throw new TexconvException("texconv could not be killed after cancellation.", ex);
        }
    }

    private static string Combine(string stdout, string stderr) => stderr.Length == 0 ? stdout : $"{stdout}{Environment.NewLine}{stderr}";

    private static string LastLine(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running {Executable} {Arguments}")]
    private partial void LogRunning(string executable, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "texconv exited with code {ExitCode} after {ElapsedMs:F0} ms")]
    private partial void LogFinished(int exitCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU BC7 encode of {Target} failed (exit code {ExitCode}); retrying with -nogpu. texconv output: {OutputTail}")]
    private partial void LogGpuEncodeFailed(string target, int? exitCode, string outputTail, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU BC7 encoder unavailable for {Target}; texconv used the CPU codec. texconv output: {OutputTail}")]
    private partial void LogGpuUnavailable(string target, string outputTail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete temporary texconv folder {Folder}")]
    private partial void LogTempCleanupFailed(string folder, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "texconv already exited before it could be killed: {Reason}")]
    private partial void LogKillSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not kill texconv after cancellation")]
    private partial void LogKillFailed(Exception exception);
}
