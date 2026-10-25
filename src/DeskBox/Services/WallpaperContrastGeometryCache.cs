namespace DeskBox.Services;

/// <summary>Caches measured local filename bounds by realized item container.</summary>
internal sealed class WallpaperContrastGeometryCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _entries;

    public WallpaperContrastGeometryCache(IEqualityComparer<TKey>? comparer = null) =>
        _entries = new Dictionary<TKey, TValue>(comparer);

    public IEnumerable<KeyValuePair<TKey, TValue>> Entries => _entries;

    public bool TryGetOrMeasure(
        TKey key,
        Func<TValue, bool> isCurrent,
        Func<(bool Success, TValue Value)> measure,
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(measure);
        if (_entries.TryGetValue(key, out value!) && isCurrent(value))
        {
            return true;
        }

        _entries.Remove(key);
        (bool Success, TValue Value) measured = measure();
        value = measured.Value;
        if (measured.Success)
        {
            _entries[key] = value;
        }

        return measured.Success;
    }

    public void Set(TKey key, TValue value) => _entries[key] = value;

    public bool Remove(TKey key) => _entries.Remove(key);

    public void Clear() => _entries.Clear();
}
