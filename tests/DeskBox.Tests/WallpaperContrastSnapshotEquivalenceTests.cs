using DeskBox.Services;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class WallpaperContrastSnapshotEquivalenceTests
{
    private static readonly WallpaperContrastRect s_bounds = new(0, 0, 100, 100);
    private static readonly WallpaperContrastRect s_dynamicBounds = new(100, 0, 200, 100);
    private static readonly Color s_background = Color.FromArgb(255, 16, 32, 48);

    [Fact]
    public void EquivalentMapsWithSeparatePixelArraysDoNotRequireANewSnapshot()
    {
        WallpaperContrastService.WallpaperSnapshot current = CreateSnapshot();
        WallpaperContrastService.WallpaperMonitor[] candidate =
        [
            CreateStaticMonitor(pixels: [1, 2, 3, 4]),
            CreateDynamicMonitor(pixels: [5, 6, 7, 8])
        ];

        Assert.True(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: candidate));
        Assert.True(current.HasSameSamplingPixels(candidate));
    }

    [Fact]
    public void StaticAndDynamicPixelChangesAreComparedByteForByte()
    {
        WallpaperContrastService.WallpaperSnapshot current = CreateSnapshot();
        WallpaperContrastService.WallpaperMonitor[] changedStatic =
        [
            CreateStaticMonitor(pixels: [1, 2, 3, 5]),
            CreateDynamicMonitor(pixels: [5, 6, 7, 8])
        ];
        WallpaperContrastService.WallpaperMonitor[] changedDynamic =
        [
            CreateStaticMonitor(pixels: [1, 2, 3, 4]),
            CreateDynamicMonitor(pixels: [5, 6, 7, 9])
        ];

        Assert.True(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: changedStatic));
        Assert.False(current.HasSameSamplingPixels(changedStatic));
        Assert.True(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: changedDynamic));
        Assert.False(current.HasSameSamplingPixels(changedDynamic));
    }

    [Fact]
    public void SamplingMetadataAndAvailabilityChangesInvalidateSnapshot()
    {
        WallpaperContrastService.WallpaperSnapshot current = CreateSnapshot();
        WallpaperContrastService.WallpaperMonitor original = CreateStaticMonitor([1, 2, 3, 4]);

        AssertMonitorMetadataChanged(current, original with
        {
            Bounds = new WallpaperContrastRect(1, 0, 101, 100)
        });
        AssertMonitorMetadataChanged(current, original with { ImagePath = @"C:\wallpapers\other.jpg" });
        AssertMonitorMetadataChanged(current, original with { ImageIdentity = "other-image-identity" });
        AssertMonitorMetadataChanged(current, original with { CaptureSource = WallpaperContrastSource.DesktopHost });
        AssertMonitorMetadataChanged(current, original with { CaptureMethod = WallpaperHostCaptureMethod.LegacyWindow });
        AssertMonitorMetadataChanged(current, original with { HasUnsupportedImage = true });
        AssertMonitorMetadataChanged(current, original with { Bitmap = null });
        AssertMonitorMetadataChanged(current, original with
        {
            Bitmap = original.Bitmap! with { MapWidth = 2, BgraPixels = [1, 2, 3, 4, 5, 6, 7, 8] }
        });
        AssertMonitorMetadataChanged(current, original with
        {
            DynamicBitmap = new WallpaperDynamicBitmap(s_bounds, 2, 1, [5, 6, 7, 8, 9, 10, 11, 12])
        });
        AssertDynamicMonitorMetadataChanged(
            current,
            CreateDynamicMonitor([5, 6, 7, 8]) with
            {
                DynamicBitmap = new WallpaperDynamicBitmap(
                    new WallpaperContrastRect(101, 0, 201, 100),
                    1,
                    1,
                    [5, 6, 7, 8])
            });
        AssertDynamicMonitorMetadataChanged(
            current,
            CreateDynamicMonitor([5, 6, 7, 8]) with
            {
                DynamicBitmap = new WallpaperDynamicBitmap(s_dynamicBounds, 2, 1, [5, 6, 7, 8, 9, 10, 11, 12])
            });

        Assert.False(current.HasSameSamplingMetadata(
            WallpaperPosition.Fill,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: [CreateStaticMonitor([1, 2, 3, 4]), CreateDynamicMonitor([5, 6, 7, 8])]));
        Assert.False(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            Color.FromArgb(255, 16, 32, 49),
            candidateReportedMonitorCount: 2,
            candidateMonitors: [CreateStaticMonitor([1, 2, 3, 4]), CreateDynamicMonitor([5, 6, 7, 8])]));
        Assert.False(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 3,
            candidateMonitors: [CreateStaticMonitor([1, 2, 3, 4]), CreateDynamicMonitor([5, 6, 7, 8])]));
    }

    private static void AssertMonitorMetadataChanged(
        WallpaperContrastService.WallpaperSnapshot current,
        WallpaperContrastService.WallpaperMonitor candidateMonitor)
    {
        Assert.False(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: [candidateMonitor, CreateDynamicMonitor([5, 6, 7, 8])]));
    }

    private static void AssertDynamicMonitorMetadataChanged(
        WallpaperContrastService.WallpaperSnapshot current,
        WallpaperContrastService.WallpaperMonitor candidateMonitor)
    {
        Assert.False(current.HasSameSamplingMetadata(
            WallpaperPosition.Center,
            s_background,
            candidateReportedMonitorCount: 2,
            candidateMonitors: [CreateStaticMonitor([1, 2, 3, 4]), candidateMonitor]));
    }

    private static WallpaperContrastService.WallpaperSnapshot CreateSnapshot() =>
        new(
            WallpaperPosition.Center,
            s_background,
            reportedMonitorCount: 2,
            monitors:
            [
                CreateStaticMonitor([1, 2, 3, 4]),
                CreateDynamicMonitor([5, 6, 7, 8])
            ],
            version: 1);

    private static WallpaperContrastService.WallpaperMonitor CreateStaticMonitor(byte[] pixels) =>
        new(
            s_bounds,
            @"C:\wallpapers\current.jpg",
            "current-image-identity",
            new WallpaperContrastService.WallpaperBitmap(100, 100, 1, 1, pixels),
            null,
            WallpaperContrastSource.SystemWallpaper,
            WallpaperHostCaptureMethod.Auto,
            HasUnsupportedImage: false);

    private static WallpaperContrastService.WallpaperMonitor CreateDynamicMonitor(byte[] pixels) =>
        new(
            s_dynamicBounds,
            @"C:\wallpapers\current.jpg",
            "current-image-identity",
            null,
            new WallpaperDynamicBitmap(s_dynamicBounds, 1, 1, pixels),
            WallpaperContrastSource.Auto,
            WallpaperHostCaptureMethod.Auto,
            HasUnsupportedImage: false);
}
