using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperContrastPolicyTests
{
    [Fact]
    public void SrgbLuminance_MapsBlackAndWhiteToEndpoints()
    {
        Assert.Equal(0d, WallpaperContrastPolicy.SrgbLuminance(0, 0, 0), 6);
        Assert.Equal(1d, WallpaperContrastPolicy.SrgbLuminance(255, 255, 255), 6);
    }

    [Fact]
    public void PreferWhite_AppliesHysteresisAroundTheContrastThreshold()
    {
        Assert.True(WallpaperContrastPolicy.PreferWhite(0.10, previousWhite: null));
        Assert.False(WallpaperContrastPolicy.PreferWhite(0.20, previousWhite: null));
        Assert.True(WallpaperContrastPolicy.PreferWhite(0.08, previousWhite: false));
        Assert.True(WallpaperContrastPolicy.PreferWhite(0.20, previousWhite: true));
        Assert.False(WallpaperContrastPolicy.PreferWhite(0.28, previousWhite: true));
        Assert.False(WallpaperContrastPolicy.PreferWhite(0.20, previousWhite: false));
    }

    [Fact]
    public void TryMapWallpaperPoint_RespectsStretchAndCenteredPlacement()
    {
        var monitor = new WallpaperContrastRect(0, 0, 100, 100);

        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Stretch,
            monitor,
            monitor,
            sourceWidth: 200,
            sourceHeight: 100,
            screenX: 25,
            screenY: 50,
            out double stretchX,
            out double stretchY));
        Assert.Equal(50, stretchX, 6);
        Assert.Equal(50, stretchY, 6);

        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Center,
            monitor,
            monitor,
            sourceWidth: 200,
            sourceHeight: 100,
            screenX: 0,
            screenY: 50,
            out double centerX,
            out double centerY));
        Assert.Equal(50, centerX, 6);
        Assert.Equal(50, centerY, 6);
    }

    [Fact]
    public void TryMapWallpaperPoint_FitUsesConfiguredBorderAndFillCropsSource()
    {
        var monitor = new WallpaperContrastRect(0, 0, 100, 100);

        Assert.False(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Fit,
            monitor,
            monitor,
            sourceWidth: 200,
            sourceHeight: 100,
            screenX: 50,
            screenY: 10,
            out _,
            out _));
        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Fill,
            monitor,
            monitor,
            sourceWidth: 200,
            sourceHeight: 100,
            screenX: 0,
            screenY: 50,
            out double sourceX,
            out double sourceY));
        Assert.Equal(50, sourceX, 6);
        Assert.Equal(50, sourceY, 6);
    }

    [Fact]
    public void TryMapWallpaperPoint_TileWrapsNegativeVirtualCoordinates()
    {
        var monitor = new WallpaperContrastRect(-100, 0, 0, 100);
        var virtualDesktop = new WallpaperContrastRect(-100, 0, 100, 100);

        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Tile,
            monitor,
            virtualDesktop,
            sourceWidth: 64,
            sourceHeight: 32,
            screenX: -70,
            screenY: 40,
            out double sourceX,
            out double sourceY));
        Assert.Equal(30, sourceX, 6);
        Assert.Equal(8, sourceY, 6);
    }

    [Fact]
    public void BlendLuminance_UsesSolidOverlayOpacity()
    {
        double whiteOverBlack = WallpaperContrastPolicy.BlendLuminance(
            0,
            255,
            255,
            255,
            0.25);

        Assert.Equal(0.25, whiteOverBlack, 6);
    }

    [Fact]
    public void TryAveragePlacedLuminance_SamplesDifferentFilenameRegionsOfOneWallpaper()
    {
        byte[] blackAndWhiteMap =
        [
            0, 0, 0, 255,
            255, 255, 255, 255
        ];
        var monitor = new WallpaperContrastRect(0, 0, 100, 20);

        Assert.True(WallpaperContrastPolicy.TryAveragePlacedLuminance(
            blackAndWhiteMap,
            mapWidth: 2,
            mapHeight: 1,
            sourceWidth: 2,
            sourceHeight: 1,
            WallpaperPosition.Stretch,
            monitor,
            monitor,
            new WallpaperContrastRect(0, 0, 40, 20),
            backgroundLuminance: 0.5,
            out double darkLabel));
        Assert.True(WallpaperContrastPolicy.TryAveragePlacedLuminance(
            blackAndWhiteMap,
            mapWidth: 2,
            mapHeight: 1,
            sourceWidth: 2,
            sourceHeight: 1,
            WallpaperPosition.Stretch,
            monitor,
            monitor,
            new WallpaperContrastRect(60, 0, 100, 20),
            backgroundLuminance: 0.5,
            out double lightLabel));

        Assert.Equal(0, darkLabel, 6);
        Assert.Equal(1, lightLabel, 6);
    }

    [Fact]
    public void TryAveragePlacedLuminance_FitLetterboxUsesDesktopBorderColor()
    {
        byte[] whiteMap = [255, 255, 255, 255];
        var monitor = new WallpaperContrastRect(0, 0, 100, 100);

        Assert.True(WallpaperContrastPolicy.TryAveragePlacedLuminance(
            whiteMap,
            mapWidth: 1,
            mapHeight: 1,
            sourceWidth: 200,
            sourceHeight: 100,
            WallpaperPosition.Fit,
            monitor,
            monitor,
            new WallpaperContrastRect(0, 0, 100, 10),
            backgroundLuminance: 0.2,
            out double luminance));

        Assert.Equal(0.2, luminance, 6);
    }

    [Fact]
    public void TryAveragePlacedLuminance_SpanMapsAcrossNegativeVirtualOrigin()
    {
        byte[] blackAndWhiteMap =
        [
            0, 0, 0, 255,
            255, 255, 255, 255
        ];
        var monitor = new WallpaperContrastRect(-100, 0, 0, 20);
        var virtualDesktop = new WallpaperContrastRect(-100, 0, 100, 20);

        Assert.True(WallpaperContrastPolicy.TryAveragePlacedLuminance(
            blackAndWhiteMap,
            mapWidth: 2,
            mapHeight: 1,
            sourceWidth: 200,
            sourceHeight: 20,
            WallpaperPosition.Span,
            monitor,
            virtualDesktop,
            new WallpaperContrastRect(-100, 0, -60, 20),
            backgroundLuminance: 0.5,
            out double negativeOriginLabel));

        Assert.Equal(0, negativeOriginLabel, 6);
    }
}
