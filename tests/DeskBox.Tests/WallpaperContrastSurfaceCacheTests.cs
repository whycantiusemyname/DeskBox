using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperContrastSurfaceCacheTests
{
    [Fact]
    public void RecycledSurfaceUpdatesOwnerAndViewMembershipWithoutRebuildingKeySnapshot()
    {
        var cache = new WallpaperContrastSurfaceCache<object, object>(
            ReferenceEqualityComparer.Instance);
        object surface = new();
        object firstOwner = new();
        object recycledOwner = new();

        Assert.True(cache.Register(surface, firstOwner, inMainView: true));
        object[] realized = cache.GetSnapshot();
        Assert.Single(realized);

        Assert.False(cache.Register(surface, recycledOwner, inMainView: false));
        Assert.Same(realized, cache.GetSnapshot());
        Assert.True(cache.TryGetOwner(surface, out object owner, out bool inMainView));
        Assert.Same(recycledOwner, owner);
        Assert.False(inMainView);
    }

    [Fact]
    public void UnregisterAndClearRemoveStrongReferencesFromCachedSnapshot()
    {
        var cache = new WallpaperContrastSurfaceCache<object, object>(
            ReferenceEqualityComparer.Instance);
        object surface = new();
        object owner = new();
        cache.Register(surface, owner, inMainView: true);
        _ = cache.GetSnapshot();

        Assert.True(cache.Unregister(surface));
        Assert.False(cache.Unregister(surface));
        Assert.Empty(cache.GetSnapshot());

        cache.Register(surface, owner, inMainView: true);
        cache.Clear();
        Assert.Empty(cache.GetSnapshot());
        Assert.False(cache.TryGetOwner(surface, out _, out _));
    }
}
