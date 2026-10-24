using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperContrastPolicyTests
{
    [Fact]
    public void CanReuseSample_RequiresTheAdaptiveForegroundToStillBeApplied()
    {
        Assert.False(WallpaperContrastPolicy.CanReuseSample(
            sampleInputsMatch: true,
            foregroundStillApplied: false));
        Assert.True(WallpaperContrastPolicy.CanReuseSample(
            sampleInputsMatch: true,
            foregroundStillApplied: true));
    }

    [Fact]
    public void HasUsableMonitors_AcceptsConnectedSubsetAndRejectsEmptyEnumeration()
    {
        Assert.True(WallpaperContrastPolicy.HasUsableMonitors(
            usableMonitorCount: 1,
            reportedMonitorCount: 3));
        Assert.False(WallpaperContrastPolicy.HasUsableMonitors(
            usableMonitorCount: 0,
            reportedMonitorCount: 3));
        Assert.False(WallpaperContrastPolicy.HasUsableMonitors(
            usableMonitorCount: 1,
            reportedMonitorCount: 0));
    }

    [Fact]
    public void HostCandidatePolicy_AllowsGenericShellRenderersAndRejectsIconAndDeskBoxBranches()
    {
        var monitor = new WallpaperContrastRect(-1920, 0, 0, 1080);
        var fullScreen = new WallpaperContrastRect(-1920, 0, 0, 1080);

        Assert.True(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "WPEDesktopCEFWindow",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: true,
            isInIconHostBranch: false,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: false,
            windowProcessId: 12,
            deskBoxProcessId: 34));
        Assert.True(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "LivelyWallpaperDesktopChild",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: true,
            isInIconHostBranch: false,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: false,
            windowProcessId: 56,
            deskBoxProcessId: 34));
        Assert.False(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "SysListView32",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: true,
            isInIconHostBranch: true,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: false,
            windowProcessId: 12,
            deskBoxProcessId: 34));
        Assert.False(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "WorkerW",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: true,
            isInIconHostBranch: false,
            hasIconHostDescendant: true,
            hasDeskBoxDescendant: false,
            windowProcessId: 12,
            deskBoxProcessId: 34));
        Assert.False(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "WorkerW",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: true,
            isInIconHostBranch: false,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: true,
            windowProcessId: 12,
            deskBoxProcessId: 34));
        Assert.False(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "WorkerW",
            fullScreen,
            monitor,
            isVisible: true,
            isInDesktopShellTree: false,
            isInIconHostBranch: false,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: false,
            windowProcessId: 12,
            deskBoxProcessId: 34));
        Assert.False(WallpaperHostCapturePolicy.IsEligibleCandidate(
            "WPEDesktopCEFWindow",
            fullScreen,
            monitor,
            isVisible: false,
            isInDesktopShellTree: true,
            isInIconHostBranch: false,
            hasIconHostDescendant: false,
            hasDeskBoxDescendant: false,
            windowProcessId: 12,
            deskBoxProcessId: 34));
    }

    [Fact]
    public void TryCreateScreenAlignedMap_CropsNegativeOriginAndPerMonitorWithoutWallpaperPlacement()
    {
        const int sourceWidth = 8;
        const int sourceHeight = 2;
        byte[] source = new byte[sourceWidth * sourceHeight * 4];
        for (int y = 0; y < sourceHeight; y++)
        for (int x = 0; x < sourceWidth; x++)
        {
            int index = ((y * sourceWidth) + x) * 4;
            source[index] = (byte)(x * 10); // B
            source[index + 1] = 0;
            source[index + 2] = 0;
            source[index + 3] = 255;
        }

        var virtualScreen = new WallpaperContrastRect(-4, 0, 4, 2);
        var leftMonitor = new WallpaperContrastRect(-4, 0, 0, 2);
        var rightMonitor = new WallpaperContrastRect(0, 0, 4, 2);

        Assert.True(WallpaperHostCapturePolicy.TryCreateScreenAlignedMap(
            source,
            sourceWidth,
            sourceHeight,
            virtualScreen,
            leftMonitor,
            maxDimension: 192,
            out WallpaperDynamicBitmap? left));
        Assert.True(WallpaperHostCapturePolicy.TryCreateScreenAlignedMap(
            source,
            sourceWidth,
            sourceHeight,
            virtualScreen,
            rightMonitor,
            maxDimension: 192,
            out WallpaperDynamicBitmap? right));
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(leftMonitor, left!.ScreenBounds);
        Assert.Equal(rightMonitor, right!.ScreenBounds);
        Assert.Equal(4, left.MapWidth);
        Assert.Equal(4, right.MapWidth);
        Assert.Equal((byte)0, left.BgraPixels[0]);
        Assert.Equal((byte)30, left.BgraPixels[(left.MapWidth - 1) * 4]);
        Assert.Equal((byte)40, right.BgraPixels[0]);
        Assert.Equal((byte)70, right.BgraPixels[(right.MapWidth - 1) * 4]);
    }

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
    public void TryMapWallpaperPoint_TileUsesOneVirtualOriginAcrossMonitors()
    {
        var leftMonitor = new WallpaperContrastRect(-100, 0, 0, 100);
        var rightMonitor = new WallpaperContrastRect(0, 0, 100, 100);
        var virtualDesktop = new WallpaperContrastRect(-100, 0, 100, 100);

        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Tile,
            leftMonitor,
            virtualDesktop,
            sourceWidth: 64,
            sourceHeight: 32,
            screenX: -10,
            screenY: 10,
            out double leftSourceX,
            out double leftSourceY));
        Assert.Equal(26, leftSourceX, 6);
        Assert.Equal(10, leftSourceY, 6);

        Assert.True(WallpaperContrastPolicy.TryMapWallpaperPoint(
            WallpaperPosition.Tile,
            rightMonitor,
            virtualDesktop,
            sourceWidth: 64,
            sourceHeight: 32,
            screenX: 10,
            screenY: 10,
            out double rightSourceX,
            out double rightSourceY));
        Assert.Equal(46, rightSourceX, 6);
        Assert.Equal(10, rightSourceY, 6);
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
