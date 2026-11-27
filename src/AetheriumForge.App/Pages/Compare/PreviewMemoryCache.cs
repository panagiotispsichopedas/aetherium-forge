namespace AetheriumForge.App.Pages.Compare;

/// <summary>
/// A thread-safe least-recently-used cache of preview samples, capped by memory. The newest sample is always kept, even when it alone
/// exceeds the cap.
/// </summary>
public sealed class PreviewMemoryCache
{
    /// <summary>The default cap: 1.5 GB.</summary>
    public const long DefaultCapacityBytes = 1536L * 1024 * 1024;

    private readonly Dictionary<string, LinkedListNode<PreviewSample>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<PreviewSample> _order = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewMemoryCache"/> class.
    /// </summary>
    /// <param name="capacityBytes">The memory cap in bytes; at least 1.</param>
    public PreviewMemoryCache(long capacityBytes = DefaultCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        CapacityBytes = capacityBytes;
    }

    /// <summary>Gets the memory cap in bytes.</summary>
    public long CapacityBytes { get; }

    /// <summary>Gets the memory currently held.</summary>
    public long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _order.Sum(sample => sample.SizeInBytes);
            }
        }
    }

    /// <summary>Gets the number of cached samples.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>Gets whether a sample with <paramref name="key"/> is cached (does not refresh its recency).</summary>
    /// <param name="key">The cache key.</param>
    /// <returns><see langword="true"/> when cached.</returns>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _index.ContainsKey(key);
        }
    }

    /// <summary>Gets a cached sample and marks it most recently used.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="sample">The sample, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGet(string key, out PreviewSample? sample)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                sample = node.Value;
                return true;
            }
        }

        sample = null;
        return false;
    }

    /// <summary>Adds or replaces a sample as most recently used, then evicts the least recently used samples above the cap.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns>The keys of the evicted samples.</returns>
    public IReadOnlyList<string> Add(PreviewSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var key = sample.Request.CacheKey;
        var evicted = new List<string>();
        lock (_gate)
        {
            if (_index.Remove(key, out var existing))
            {
                _order.Remove(existing);
            }

            _index[key] = _order.AddFirst(sample);
            var total = _order.Sum(item => item.SizeInBytes);
            while (total > CapacityBytes && _order.Count > 1)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                var oldestKey = oldest.Value.Request.CacheKey;
                _index.Remove(oldestKey);
                total -= oldest.Value.SizeInBytes;
                evicted.Add(oldestKey);
            }
        }

        return evicted;
    }

    /// <summary>Removes every sample.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
        }
    }
}
