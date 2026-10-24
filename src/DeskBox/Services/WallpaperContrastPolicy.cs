namespace DeskBox.Services;

internal enum WallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}

internal readonly record struct WallpaperContrastRect(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    public double Area => Width * Height;

    public WallpaperContrastRect Intersect(WallpaperContrastRect other) => new(
        Math.Max(Left, other.Left),
        Math.Max(Top, other.Top),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));
}

/// <summary>Pure color and placement math used by wallpaper contrast sampling.</summary>
internal static class WallpaperContrastPolicy
{
    public static double SrgbLuminance(byte red, byte green, byte blue)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(red)) + (0.7152 * Linear(green)) + (0.0722 * Linear(blue));
    }

    /// <summary>
    /// Approximates a translucent solid overlay by blending linear luminance;
    /// it avoids per-pixel RGB compositing for each filename sample.
    /// </summary>
    public static double BlendLuminance(double backgroundLuminance, byte red, byte green, byte blue, double opacity)
    {
        double alpha = Math.Clamp(opacity, 0, 1);
        return (Math.Clamp(backgroundLuminance, 0, 1) * (1 - alpha)) + (SrgbLuminance(red, green, blue) * alpha);
    }

    /// <summary>
    /// Maps a physical desktop point to source-image pixel coordinates using
    /// the Windows desktop wallpaper placement rules. False means that Fit or
    /// Center left this point on the configured solid border color.
    /// </summary>
    public static bool TryMapWallpaperPoint(
        WallpaperPosition position,
        WallpaperContrastRect monitorBounds,
        WallpaperContrastRect virtualDesktopBounds,
        int sourceWidth,
        int sourceHeight,
        double screenX,
        double screenY,
        out double sourceX,
        out double sourceY)
    {
        sourceX = 0;
        sourceY = 0;
        if (sourceWidth <= 0 || sourceHeight <= 0 || monitorBounds.Width <= 0 || monitorBounds.Height <= 0 ||
            !double.IsFinite(screenX) || !double.IsFinite(screenY)) return false;

        WallpaperContrastRect bounds = position is WallpaperPosition.Span or WallpaperPosition.Tile
            ? virtualDesktopBounds
            : monitorBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        if (position == WallpaperPosition.Tile)
        {
            sourceX = PositiveModulo(screenX - bounds.Left, sourceWidth);
            sourceY = PositiveModulo(screenY - bounds.Top, sourceHeight);
            return true;
        }

        if (position == WallpaperPosition.Stretch)
        {
            if (screenX < bounds.Left || screenX >= bounds.Right ||
                screenY < bounds.Top || screenY >= bounds.Bottom) return false;
            sourceX = (screenX - bounds.Left) * sourceWidth / bounds.Width;
            sourceY = (screenY - bounds.Top) * sourceHeight / bounds.Height;
            return true;
        }

        double scaleX = bounds.Width / sourceWidth;
        double scaleY = bounds.Height / sourceHeight;
        double scale = position switch
        {
            WallpaperPosition.Fit => Math.Min(scaleX, scaleY),
            WallpaperPosition.Fill or WallpaperPosition.Span => Math.Max(scaleX, scaleY),
            WallpaperPosition.Center => 1,
            _ => double.NaN
        };
        if (!double.IsFinite(scale) || scale <= 0) return false;

        double displayedWidth = sourceWidth * scale;
        double displayedHeight = sourceHeight * scale;
        double originX = bounds.Left + ((bounds.Width - displayedWidth) / 2);
        double originY = bounds.Top + ((bounds.Height - displayedHeight) / 2);
        if (screenX < originX || screenX >= originX + displayedWidth ||
            screenY < originY || screenY >= originY + displayedHeight) return false;

        sourceX = (screenX - originX) / scale;
        sourceY = (screenY - originY) / scale;
        return sourceX >= 0 && sourceY >= 0 && sourceX < sourceWidth && sourceY < sourceHeight;
    }

    /// <summary>Samples a bounded grid over a screen rectangle and includes wallpaper border color.</summary>
    public static bool TryAveragePlacedLuminance(
        ReadOnlySpan<byte> bgra,
        int mapWidth,
        int mapHeight,
        int sourceWidth,
        int sourceHeight,
        WallpaperPosition position,
        WallpaperContrastRect monitorBounds,
        WallpaperContrastRect virtualDesktopBounds,
        WallpaperContrastRect screenBounds,
        double backgroundLuminance,
        out double luminance)
    {
        luminance = 0;
        if (mapWidth <= 0 || mapHeight <= 0 || bgra.Length < (long)mapWidth * mapHeight * 4 ||
            sourceWidth <= 0 || sourceHeight <= 0 || screenBounds.Width <= 0 || screenBounds.Height <= 0 ||
            !double.IsFinite(backgroundLuminance)) return false;

        int columns = Math.Clamp((int)Math.Ceiling(screenBounds.Width / 18), 1, 32);
        int rows = Math.Clamp((int)Math.Ceiling(screenBounds.Height / 18), 1, 16);
        double sum = 0;
        int count = 0;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            double x = screenBounds.Left + ((column + 0.5) * screenBounds.Width / columns);
            double y = screenBounds.Top + ((row + 0.5) * screenBounds.Height / rows);
            double sample = Math.Clamp(backgroundLuminance, 0, 1);
            if (TryMapWallpaperPoint(position, monitorBounds, virtualDesktopBounds,
                    sourceWidth, sourceHeight, x, y, out double sourceX, out double sourceY))
            {
                int mapX = Math.Clamp((int)(sourceX * mapWidth / sourceWidth), 0, mapWidth - 1);
                int mapY = Math.Clamp((int)(sourceY * mapHeight / sourceHeight), 0, mapHeight - 1);
                int index = ((mapY * mapWidth) + mapX) * 4;
                sample = SrgbLuminance(bgra[index + 2], bgra[index + 1], bgra[index]);
            }

            sum += sample;
            count++;
        }

        luminance = count > 0 ? sum / count : 0;
        return count > 0;
    }

    /// <summary>Returns true for white foreground. Keeps the previous choice in the transition band.</summary>
    public static bool PreferWhite(double luminance, bool? previousWhite, double hysteresis = 0.08)
    {
        double threshold = 0.179;
        double band = Math.Clamp(hysteresis, 0, 0.5);
        luminance = Math.Clamp(luminance, 0, 1);
        if (previousWhite is null)
        {
            // Start from the foreground with the better WCAG contrast ratio;
            // hysteresis applies only after a choice exists.
            return luminance < threshold;
        }

        if (luminance < threshold - band) return true;
        if (luminance > threshold + band) return false;
        return previousWhite.Value;
    }

    /// <summary>Computes the average linear luminance for a clipped region of a BGRA map.</summary>
    public static bool TryAverageLuminance(ReadOnlySpan<byte> bgra, int width, int height,
        double left, double top, double right, double bottom, out double luminance)
    {
        luminance = 0;
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4 ||
            !double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom)) return false;
        int x0 = Math.Clamp((int)Math.Floor(left), 0, width);
        int y0 = Math.Clamp((int)Math.Floor(top), 0, height);
        int x1 = Math.Clamp((int)Math.Ceiling(right), 0, width);
        int y1 = Math.Clamp((int)Math.Ceiling(bottom), 0, height);
        if (x1 <= x0 || y1 <= y0) return false;

        double sum = 0;
        long count = 0;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = ((y * width) + x) * 4;
            sum += SrgbLuminance(bgra[i + 2], bgra[i + 1], bgra[i]);
            count++;
        }
        luminance = sum / count;
        return count > 0;
    }

    private static double PositiveModulo(double value, double divisor)
    {
        double remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
