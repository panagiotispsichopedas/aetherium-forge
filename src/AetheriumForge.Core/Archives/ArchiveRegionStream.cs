using Microsoft.Win32.SafeHandles;

namespace AetheriumForge.Core.Archives;

/// <summary>
/// A read-only, seekable view over a byte range of a file, backed by positional reads on a shared handle.
/// </summary>
/// <remarks>
/// Every instance keeps its own position and uses <see cref="RandomAccess"/>, so many instances can read the same
/// handle concurrently. The stream never owns or closes the handle.
/// </remarks>
internal sealed class ArchiveRegionStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// Initializes a new view of <paramref name="length"/> bytes starting at <paramref name="start"/>.
    /// </summary>
    /// <param name="handle">The file handle to read from. It must outlive this stream.</param>
    /// <param name="start">Absolute start offset of the region.</param>
    /// <param name="length">Length of the region in bytes.</param>
    public ArchiveRegionStream(SafeFileHandle handle, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _handle = handle;
        _start = start;
        _length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        int read = RandomAccess.Read(_handle, buffer, _start + _position);
        _position += read;

        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
        };

        Position = target;

        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("The archive region stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The archive region stream is read-only.");
}
