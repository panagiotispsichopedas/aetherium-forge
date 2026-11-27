namespace AetheriumForge.Core.Projects;

/// <summary>
/// A user-facing project error (no project found, a project folder inside the game folder, no drive available). The message says what
/// went wrong and, ideally, how to fix it; front ends show it as is, without a stack trace.
/// </summary>
public sealed class ProjectException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProjectException"/> class.</summary>
    public ProjectException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProjectException"/> class.</summary>
    /// <param name="message">What went wrong and, ideally, how to fix it.</param>
    public ProjectException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProjectException"/> class.</summary>
    /// <param name="message">What went wrong and, ideally, how to fix it.</param>
    /// <param name="innerException">The underlying error.</param>
    public ProjectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
