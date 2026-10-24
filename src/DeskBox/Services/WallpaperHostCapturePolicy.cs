namespace DeskBox.Services;

/// <summary>Pure structural and pixel mapping rules for desktop host capture.</summary>
internal static class WallpaperHostCapturePolicy
{
    public static bool IsIconHostClass(string className) =>
        string.Equals(className, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "SysListView32", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "FolderView", StringComparison.OrdinalIgnoreCase);

    public static bool IsEligibleCandidate(
        string className,
        WallpaperContrastRect windowBounds,
        WallpaperContrastRect monitorBounds,
        bool isVisible,
        bool isInDesktopShellTree,
        bool isInIconHostBranch,
        bool hasIconHostDescendant,
        bool hasDeskBoxDescendant,
        uint windowProcessId,
        uint deskBoxProcessId)
    {
        if (string.IsNullOrWhiteSpace(className) ||
            !isVisible ||
            !isInDesktopShellTree ||
            isInIconHostBranch ||
            IsIconHostClass(className) ||
            hasIconHostDescendant ||
            hasDeskBoxDescendant ||
            (deskBoxProcessId != 0 && windowProcessId == deskBoxProcessId))
        {
            return false;
        }

        return windowBounds.Width > 0 && windowBounds.Height > 0 &&
               monitorBounds.Width > 0 && monitorBounds.Height > 0 &&
               windowBounds.Left <= monitorBounds.Left &&
               windowBounds.Top <= monitorBounds.Top &&
               windowBounds.Right >= monitorBounds.Right &&
               windowBounds.Bottom >= monitorBounds.Bottom;
    }

    /// <summary>
    /// Produces a bounded BGRA map whose pixels remain aligned to the given
    /// physical desktop rectangle. The source rectangle may include adjacent
    /// monitors; it is cropped by screen coordinates without wallpaper fill.
    /// </summary>
    public static bool TryCreateScreenAlignedMap(
        ReadOnlySpan<byte> sourceBgra,
        int sourceWidth,
        int sourceHeight,
        WallpaperContrastRect sourceBounds,
        WallpaperContrastRect targetBounds,
        int maxDimension,
        out WallpaperDynamicBitmap? bitmap)
    {
        bitmap = null;
        if (sourceWidth <= 0 || sourceHeight <= 0 || maxDimension <= 0 ||
            sourceBgra.Length < (long)sourceWidth * sourceHeight * 4 ||
            sourceBounds.Width <= 0 || sourceBounds.Height <= 0 ||
            targetBounds.Width <= 0 || targetBounds.Height <= 0 ||
            sourceBounds.Left > targetBounds.Left ||
            sourceBounds.Top > targetBounds.Top ||
            sourceBounds.Right < targetBounds.Right ||
            sourceBounds.Bottom < targetBounds.Bottom)
        {
            return false;
        }

        double scale = Math.Min(1d, maxDimension / Math.Max(targetBounds.Width, targetBounds.Height));
        int mapWidth = Math.Max(1, (int)Math.Round(targetBounds.Width * scale));
        int mapHeight = Math.Max(1, (int)Math.Round(targetBounds.Height * scale));
        if ((long)mapWidth * mapHeight > (long)maxDimension * maxDimension)
        {
            return false;
        }

        byte[] pixels = new byte[checked(mapWidth * mapHeight * 4)];
        for (int y = 0; y < mapHeight; y++)
        {
            double screenY = targetBounds.Top + ((y + 0.5) * targetBounds.Height / mapHeight);
            int sourceY = Math.Clamp(
                (int)((screenY - sourceBounds.Top) * sourceHeight / sourceBounds.Height),
                0,
                sourceHeight - 1);
            for (int x = 0; x < mapWidth; x++)
            {
                double screenX = targetBounds.Left + ((x + 0.5) * targetBounds.Width / mapWidth);
                int sourceX = Math.Clamp(
                    (int)((screenX - sourceBounds.Left) * sourceWidth / sourceBounds.Width),
                    0,
                    sourceWidth - 1);
                int sourceIndex = ((sourceY * sourceWidth) + sourceX) * 4;
                int targetIndex = ((y * mapWidth) + x) * 4;
                sourceBgra.Slice(sourceIndex, 4).CopyTo(pixels.AsSpan(targetIndex, 4));
            }
        }

        bitmap = new WallpaperDynamicBitmap(
            targetBounds,
            mapWidth,
            mapHeight,
            pixels);
        return true;
    }

    public static bool TryAverageScreenAlignedLuminance(
        WallpaperDynamicBitmap bitmap,
        WallpaperContrastRect screenBounds,
        out double luminance)
    {
        luminance = 0;
        WallpaperContrastRect sampleBounds = screenBounds.Intersect(bitmap.ScreenBounds);
        if (bitmap.MapWidth <= 0 || bitmap.MapHeight <= 0 ||
            bitmap.BgraPixels.Length < (long)bitmap.MapWidth * bitmap.MapHeight * 4 ||
            sampleBounds.Area <= 0)
        {
            return false;
        }

        int columns = Math.Clamp((int)Math.Ceiling(sampleBounds.Width / 18), 1, 32);
        int rows = Math.Clamp((int)Math.Ceiling(sampleBounds.Height / 18), 1, 16);
        double sum = 0;
        int count = 0;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            double screenX = sampleBounds.Left + ((column + 0.5) * sampleBounds.Width / columns);
            double screenY = sampleBounds.Top + ((row + 0.5) * sampleBounds.Height / rows);
            int mapX = Math.Clamp(
                (int)((screenX - bitmap.ScreenBounds.Left) * bitmap.MapWidth / bitmap.ScreenBounds.Width),
                0,
                bitmap.MapWidth - 1);
            int mapY = Math.Clamp(
                (int)((screenY - bitmap.ScreenBounds.Top) * bitmap.MapHeight / bitmap.ScreenBounds.Height),
                0,
                bitmap.MapHeight - 1);
            int index = ((mapY * bitmap.MapWidth) + mapX) * 4;
            sum += WallpaperContrastPolicy.SrgbLuminance(
                bitmap.BgraPixels[index + 2],
                bitmap.BgraPixels[index + 1],
                bitmap.BgraPixels[index]);
            count++;
        }

        luminance = count > 0 ? sum / count : 0;
        return count > 0;
    }
}

internal sealed record WallpaperDynamicBitmap(
    WallpaperContrastRect ScreenBounds,
    int MapWidth,
    int MapHeight,
    byte[] BgraPixels);
