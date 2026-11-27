namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Thrown by an <see cref="IModelRunner"/> when the device cannot allocate memory for a tile. A smaller tile may succeed.
/// </summary>
public sealed class VideoMemoryExhaustedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="VideoMemoryExhaustedException"/> class.</summary>
    public VideoMemoryExhaustedException()
        : base("The device ran out of memory.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VideoMemoryExhaustedException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public VideoMemoryExhaustedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VideoMemoryExhaustedException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The runtime error that reported the allocation failure.</param>
    public VideoMemoryExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
