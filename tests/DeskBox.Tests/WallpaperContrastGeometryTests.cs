using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperContrastGeometryTests
{
    [Fact]
    public void GridTextBounds_UsesRowColumnCellAndScrollOffsets()
    {
        WallpaperContrastRect? bounds = WallpaperContrastGeometry.GetGridTextBounds(
            itemIndex: 7,
            columns: 3,
            cellWidth: 100,
            cellHeight: 80,
            tileMarginLeft: 5,
            tileMarginTop: 2,
            textBoundsInTile: new WallpaperContrastRect(10, 20, 50, 40),
            gridLeftInViewport: 4,
            gridTopInViewport: 3,
            horizontalOffset: 2,
            verticalOffset: 15,
            viewportWidth: 250,
            viewportHeight: 200);

        Assert.Equal(new WallpaperContrastRect(117, 170, 157, 190), bounds);
    }

    [Fact]
    public void GridTextBounds_UsesUpdatedIndexAfterSortAndClipsToViewport()
    {
        WallpaperContrastRect? bounds = WallpaperContrastGeometry.GetGridTextBounds(
            itemIndex: 4,
            columns: 3,
            cellWidth: 100,
            cellHeight: 80,
            tileMarginLeft: 0,
            tileMarginTop: 0,
            textBoundsInTile: new WallpaperContrastRect(10, 10, 60, 30),
            gridLeftInViewport: 0,
            gridTopInViewport: 0,
            horizontalOffset: 0,
            verticalOffset: 0,
            viewportWidth: 150,
            viewportHeight: 120);

        Assert.Equal(new WallpaperContrastRect(110, 90, 150, 110), bounds);
    }

    [Fact]
    public void VariableHeightListBounds_ShiftByScrollDeltaAndClip()
    {
        WallpaperContrastRect? bounds = WallpaperContrastGeometry.GetScrolledListTextBounds(
            textBoundsInViewport: new WallpaperContrastRect(12, 118, 142, 142),
            measuredHorizontalOffset: 0,
            measuredVerticalOffset: 100,
            currentHorizontalOffset: 0,
            currentVerticalOffset: 145,
            viewportWidth: 160,
            viewportHeight: 80);

        Assert.Equal(new WallpaperContrastRect(12, 73, 142, 80), bounds);
    }

    [Fact]
    public void ToScreen_AppliesWindowOffsetAndDpiScale()
    {
        WallpaperContrastRect bounds = WallpaperContrastGeometry.ToScreen(
            new WallpaperContrastRect(8, 12, 18, 22),
            viewportOffsetX: 0,
            viewportOffsetY: 0,
            clientOriginX: 100,
            clientOriginY: 200,
            scale: 1.5);

        Assert.Equal(new WallpaperContrastRect(112, 218, 127, 233), bounds);
    }

    [Fact]
    public void ToScreen_AppliesScrolledViewportOffsetOnceAt125PercentDpi()
    {
        WallpaperContrastRect? scrolledBounds = WallpaperContrastGeometry.GetScrolledListTextBounds(
            textBoundsInViewport: new WallpaperContrastRect(30, 60, 50, 80),
            measuredHorizontalOffset: 0,
            measuredVerticalOffset: 10,
            currentHorizontalOffset: 0,
            currentVerticalOffset: 15,
            viewportWidth: 100,
            viewportHeight: 100);

        Assert.Equal(new WallpaperContrastRect(30, 55, 50, 75), scrolledBounds);
        WallpaperContrastRect screenBounds = WallpaperContrastGeometry.ToScreen(
            scrolledBounds!.Value,
            viewportOffsetX: 24,
            viewportOffsetY: 18,
            clientOriginX: 100,
            clientOriginY: 200,
            scale: 1.25);

        Assert.Equal(new WallpaperContrastRect(167.5, 291.25, 192.5, 316.25), screenBounds);
    }
}
