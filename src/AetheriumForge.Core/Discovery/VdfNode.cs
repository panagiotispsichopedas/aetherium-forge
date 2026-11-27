namespace AetheriumForge.Core.Discovery;

/// <summary>
/// One key of a Valve KeyValues (VDF/ACF) document: either a string value or a block of child keys.
/// </summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children = [];

    /// <summary>
    /// Initialises a block node.
    /// </summary>
    /// <param name="key">The key name.</param>
    public VdfNode(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Key = key;
    }

    /// <summary>
    /// Initialises a value node.
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <param name="value">The string value.</param>
    public VdfNode(string key, string value)
        : this(key)
    {
        ArgumentNullException.ThrowIfNull(value);

        Value = value;
    }

    /// <summary>Gets the key name as written in the file.</summary>
    public string Key { get; }

    /// <summary>Gets the string value, or <see langword="null"/> for a block node.</summary>
    public string? Value { get; }

    /// <summary>Gets the child keys in file order (empty for a value node).</summary>
    public IReadOnlyList<VdfNode> Children => _children;

    /// <summary>
    /// Finds the first direct child whose key matches <paramref name="key"/>, ignoring case (KeyValues keys are case-insensitive).
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <returns>The child, or <see langword="null"/> when there is none.</returns>
    public VdfNode? this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);

            return _children.Find(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal void Add(VdfNode child) => _children.Add(child);
}
