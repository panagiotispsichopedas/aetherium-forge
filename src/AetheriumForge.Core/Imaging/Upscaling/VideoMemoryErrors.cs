using Microsoft.ML.OnnxRuntime;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Recognises out-of-memory failures reported by ONNX Runtime.
/// </summary>
/// <remarks>
/// ONNX Runtime has no dedicated error code for allocation failures; every provider reports them as
/// <see cref="OnnxRuntimeException"/> (usually <c>[ErrorCode:RuntimeException]</c> or <c>[ErrorCode:Fail]</c>) and only the
/// message identifies the cause. The DirectML provider wraps the failing <c>HRESULT</c> in the message, for example
/// <c>... DmlCommittedResourceAllocator.cpp(22) ... Exception(3) tid(1f24) 8007000E Not enough memory resources are available to complete this operation.</c>
/// (<c>E_OUTOFMEMORY</c>). The CPU and arena allocators report <c>Failed to allocate memory for requested buffer of size ...</c>
/// or a C++ <c>bad allocation</c>. Device removal (<c>887A0005</c>/<c>887A0006</c>) is deliberately not treated as out of memory:
/// the session is unusable afterwards and a smaller tile cannot help.
/// </remarks>
internal static class VideoMemoryErrors
{
    private static readonly string[] _markers =
    [
        "8007000E",
        "E_OUTOFMEMORY",
        "out of memory",
        "outofmemory",
        "Not enough memory",
        "Failed to allocate memory",
        "bad allocation",
        "bad_alloc",
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="exception"/> (or an inner exception) reports an allocation failure.
    /// </summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns><see langword="true"/> for out-of-memory failures.</returns>
    public static bool IsOutOfMemory(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VideoMemoryExhaustedException)
            {
                return true;
            }

            if (current is OnnxRuntimeException && MessageIndicatesOutOfMemory(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when an ONNX Runtime error message describes an allocation failure.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns><see langword="true"/> when a known out-of-memory marker is present.</returns>
    public static bool MessageIndicatesOutOfMemory(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var marker in _markers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
