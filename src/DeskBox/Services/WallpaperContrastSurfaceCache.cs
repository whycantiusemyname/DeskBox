namespace DeskBox.Services;

/// <summary>
/// Caches realized wallpaper-contrast surface owners and the membership snapshot
/// used by sampling. Updating owner metadata does not rebuild the stable key list.
/// </summary>
internal sealed class WallpaperContrastSurfaceCache<TSurface, TOwner>
    where TSurface : notnull
    where TOwner : notnull
{
    private readonly Dictionary<TSurface, SurfaceEntry> _entries;
    private TSurface[]? _snapshot;

    public WallpaperContrastSurfaceCache(IEqualityComparer<TSurface>? comparer = null) =>
        _entries = new Dictionary<TSurface, SurfaceEntry>(comparer);

    public int Count => _entries.Count;

    public TSurface[] GetSnapshot() => _snapshot ??= _entries.Keys.ToArray();

    public bool Register(TSurface surface, TOwner owner, bool inMainView)
    {
        bool added = !_entries.ContainsKey(surface);
        _entries[surface] = new SurfaceEntry(owner, inMainView);
        if (added)
        {
            _snapshot = null;
        }

        return added;
    }

    public bool TryGetOwner(
        TSurface surface,
        out TOwner owner,
        out bool inMainView)
    {
        if (_entries.TryGetValue(surface, out SurfaceEntry? entry) && entry is not null)
        {
            owner = entry.Owner;
            inMainView = entry.InMainView;
            return true;
        }

        owner = default!;
        inMainView = false;
        return false;
    }

    public bool Unregister(TSurface surface)
    {
        if (!_entries.Remove(surface))
        {
            return false;
        }

        _snapshot = null;
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _snapshot = null;
    }

    private sealed record SurfaceEntry(TOwner Owner, bool InMainView);
}
