namespace DeskBox.Services;

/// <summary>Pure UI-local geometry math shared by event-driven contrast sampling.</summary>
internal static class WallpaperContrastGeometry
{
    public static WallpaperContrastRect? GetGridTextBounds(
        int itemIndex,
        int columns,
        double cellWidth,
        double cellHeight,
        double tileMarginLeft,
        double tileMarginTop,
        WallpaperContrastRect textBoundsInTile,
        double gridLeftInViewport,
        double gridTopInViewport,
        double horizontalOffset,
        double verticalOffset,
        double viewportWidth,
        double viewportHeight)
    {
        if (itemIndex < 0 || columns <= 0 ||
            !double.IsFinite(cellWidth) || cellWidth <= 0 ||
            !double.IsFinite(cellHeight) || cellHeight <= 0 ||
            viewportWidth <= 0 || viewportHeight <= 0 ||
            textBoundsInTile.Width <= 0 || textBoundsInTile.Height <= 0)
        {
            return null;
        }

        int column = itemIndex % columns;
        int row = itemIndex / columns;
        double left = gridLeftInViewport + (column * cellWidth) + tileMarginLeft +
            textBoundsInTile.Left - horizontalOffset;
        double top = gridTopInViewport + (row * cellHeight) + tileMarginTop +
            textBoundsInTile.Top - verticalOffset;
        return Intersect(
            new WallpaperContrastRect(
                left,
                top,
                left + textBoundsInTile.Width,
                top + textBoundsInTile.Height),
            new WallpaperContrastRect(0, 0, viewportWidth, viewportHeight));
    }

    public static WallpaperContrastRect? GetScrolledListTextBounds(
        WallpaperContrastRect textBoundsInViewport,
        double measuredHorizontalOffset,
        double measuredVerticalOffset,
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double viewportWidth,
        double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 ||
            textBoundsInViewport.Width <= 0 || textBoundsInViewport.Height <= 0)
        {
            return null;
        }

        double horizontalDelta = measuredHorizontalOffset - currentHorizontalOffset;
        double verticalDelta = measuredVerticalOffset - currentVerticalOffset;
        return Intersect(
            new WallpaperContrastRect(
                textBoundsInViewport.Left + horizontalDelta,
                textBoundsInViewport.Top + verticalDelta,
                textBoundsInViewport.Right + horizontalDelta,
                textBoundsInViewport.Bottom + verticalDelta),
            new WallpaperContrastRect(0, 0, viewportWidth, viewportHeight));
    }

    public static WallpaperContrastRect ToScreen(
        WallpaperContrastRect viewportBounds,
        double viewportOffsetX,
        double viewportOffsetY,
        double clientOriginX,
        double clientOriginY,
        double scale) => new(
            clientOriginX + (viewportOffsetX + viewportBounds.Left) * scale,
            clientOriginY + (viewportOffsetY + viewportBounds.Top) * scale,
            clientOriginX + (viewportOffsetX + viewportBounds.Right) * scale,
            clientOriginY + (viewportOffsetY + viewportBounds.Bottom) * scale);

    public static WallpaperContrastRect ToScreen(
        WallpaperContrastRect rootBounds,
        double clientOriginX,
        double clientOriginY,
        double scale) => ToScreen(
            rootBounds,
            viewportOffsetX: 0,
            viewportOffsetY: 0,
            clientOriginX: clientOriginX,
            clientOriginY: clientOriginY,
            scale: scale);

    private static WallpaperContrastRect? Intersect(
        WallpaperContrastRect left,
        WallpaperContrastRect right)
    {
        WallpaperContrastRect intersection = left.Intersect(right);
        return intersection.Width > 0 && intersection.Height > 0
            ? intersection
            : null;
    }
}
