using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AetheriumForge.Core.Models;

namespace AetheriumForge.Core.Tests.Models;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a delegate and records every request.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = [];

    public IEnumerable<Uri> RequestedUrls => Requests.Select(request => request.RequestUri!);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var response = _respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// A read-only stream that returns at most <see cref="ChunkSize"/> bytes per read and counts reads.
/// </summary>
internal sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
{
    private int _position;

    public int ChunkSize { get; } = chunkSize;

    public int Reads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        Reads++;
        var count = Math.Min(Math.Min(buffer.Length, ChunkSize), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> so tests can act on a report before the next chunk is read.
/// </summary>
internal sealed class SyncProgress(Action<DownloadProgress> onReport) : IProgress<DownloadProgress>
{
    public List<DownloadProgress> Reports { get; } = [];

    public void Report(DownloadProgress value)
    {
        Reports.Add(value);
        onReport(value);
    }
}

/// <summary>
/// Builders for manifests, models and HTTP responses.
/// </summary>
internal static class ModelTestData
{
    public const string LicenceText = "BSD 3-Clause License (test)";

    public static readonly Uri LicenceUrl = new("https://github.com/o/r/releases/download/models-v1/LICENSE.txt");

    public static byte[] RandomBytes(int length, int seed = 42)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static ManifestModel Model(string name, byte[] data, string? sha256 = null, long? size = null) => new(
        Name: name,
        Family: name,
        DisplayName: $"{name} display",
        Description: "Test model",
        Architecture: "Test",
        ParametersMillions: 1.2,
        FileName: $"{name}.onnx",
        Url: new Uri($"https://github.com/o/r/releases/download/models-v1/{name}.onnx"),
        Size: size ?? data.Length,
        Sha256: sha256 ?? Sha256(data),
        SidecarFileName: $"{name}.json",
        Scale: 4,
        Precision: "fp16",
        Tier: "low",
        Licence: "BSD-3-Clause",
        LicenceFile: "LICENSE.txt",
        LicenceUrl: LicenceUrl,
        RecommendedFor: ["diffuse", "normal"],
        Source: "https://example.invalid/source",
        Input: "input",
        Output: "output",
        Opset: 17,
        MaxAbsDiffVsPyTorch: 0.001);

    public static string ManifestJson(string release, params (string Name, string Sha256, long Size)[] models)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $$"""{ "schemaVersion": 1, "release": "{{release}}", "published": "2026-11-27", "futureField": { "x": 1 }, "models": [""");
        for (var index = 0; index < models.Length; index++)
        {
            var (name, sha, size) = models[index];
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $$"""
                {
                  "name": "{{name}}", "family": "{{name}}", "displayName": "{{name}} display", "description": "d", "architecture": "SRVGGNetCompact",
                  "parametersMillions": 1.2, "fileName": "{{name}}.onnx",
                  "url": "https://github.com/o/r/releases/download/{{release}}/{{name}}.onnx",
                  "size": {{size}}, "sha256": "{{sha}}", "sidecarFileName": "{{name}}.json", "scale": 4, "precision": "fp32",
                  "tier": "low", "licence": "BSD-3-Clause", "licenceFile": "LICENSE.txt",
                  "licenceUrl": "https://github.com/o/r/releases/download/{{release}}/LICENSE.txt",
                  "recommendedFor": ["diffuse", "normal"], "source": "s", "input": "input", "output": "output", "opset": 17,
                  "maxAbsDiffVsPyTorch": 5E-06, "unknownModelField": true
                }
                """);
        }

        builder.Append("] }");
        return builder.ToString();
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    /// <summary>Serves <paramref name="data"/> honouring a <c>Range</c> header when <paramref name="honourRange"/> is set.</summary>
    public static HttpResponseMessage Serve(HttpRequestMessage request, byte[] data, bool honourRange = true, int chunkSize = 4096, Action<ChunkedStream>? onStream = null)
    {
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        if (honourRange && from is > 0)
        {
            var start = (int)from.Value;
            if (start >= data.Length)
            {
                return Status(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            var slice = data[start..];
            var partial = new ChunkedStream(slice, chunkSize);
            onStream?.Invoke(partial);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(partial) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return response;
        }

        var stream = new ChunkedStream(data, chunkSize);
        onStream?.Invoke(stream);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    }

    /// <summary>Answers licence requests; everything else goes to <paramref name="next"/>.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> WithLicence(Func<HttpRequestMessage, HttpResponseMessage> next) =>
        request => request.RequestUri!.AbsolutePath.EndsWith("/LICENSE.txt", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LicenceText) }
            : next(request);
}

/// <summary>
/// A temporary folder deleted on dispose.
/// </summary>
internal sealed class TempModelsFolder : IDisposable
{
    public TempModelsFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));
        ModelsFolder = Path.Combine(Root, "models");
        Directory.CreateDirectory(ModelsFolder);
    }

    public string Root { get; }

    public string ModelsFolder { get; }

    public string CachePath => Path.Combine(Root, "models-manifest.cache.json");

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
