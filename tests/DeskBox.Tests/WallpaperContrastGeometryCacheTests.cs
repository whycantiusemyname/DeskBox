using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperContrastGeometryCacheTests
{
    [Fact]
    public void StaticContainerReusesMeasurementAndRecycledContainerMeasuresItsNewItem()
    {
        var cache = new WallpaperContrastGeometryCache<object, TestGeometry>();
        object container = new();
        object item = new();
        object label = new();
        int measurements = 0;

        bool TryGetCurrent(out TestGeometry geometry) => cache.TryGetOrMeasure(
            container,
            cached => ReferenceEquals(cached.Item, item) &&
                ReferenceEquals(cached.Label, label),
            () =>
            {
                measurements++;
                return (true, new TestGeometry(item, label, measurements));
            },
            out geometry);

        Assert.True(TryGetCurrent(out TestGeometry first));
        Assert.True(TryGetCurrent(out TestGeometry second));
        Assert.Equal(first, second);
        Assert.Equal(1, measurements);

        item = new object();
        label = new object();
        Assert.True(TryGetCurrent(out TestGeometry recycled));
        Assert.Same(item, recycled.Item);
        Assert.Same(label, recycled.Label);
        Assert.Equal(2, measurements);
    }

    private readonly record struct TestGeometry(object Item, object Label, int Measurement);
}
