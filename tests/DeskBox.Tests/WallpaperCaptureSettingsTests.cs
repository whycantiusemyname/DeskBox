using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperCaptureSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void VersionFifteenMigration_AddsDefaultsAndNormalizesInvalidAndOutOfRangeValues()
    {
        AppSettings oldSettings = new() { SchemaVersion = 15 };
        (AppSettings defaults, bool defaultsApplied) =
            new SettingsMigrationPipeline().RunMigrationsOnCopy(oldSettings);

        Assert.True(defaultsApplied);
        Assert.Equal(17, defaults.SchemaVersion);
        Assert.Equal("Auto", defaults.WallpaperCaptureSource);
        Assert.Equal("Auto", defaults.WallpaperHostCaptureMethod);
        Assert.Equal(60, defaults.WallpaperCaptureRefreshIntervalSeconds);

        AppSettings invalid = new()
        {
            SchemaVersion = 15,
            WallpaperCaptureSource = "UnknownSource",
            WallpaperHostCaptureMethod = "UnknownMethod",
            WallpaperCaptureRefreshIntervalSeconds = 1
        };
        (AppSettings normalizedMinimum, _) =
            new SettingsMigrationPipeline().RunMigrationsOnCopy(invalid);
        Assert.Equal("Auto", normalizedMinimum.WallpaperCaptureSource);
        Assert.Equal("Auto", normalizedMinimum.WallpaperHostCaptureMethod);
        Assert.Equal(1, normalizedMinimum.WallpaperCaptureRefreshIntervalSeconds);

        AppSettings validAndHigh = new()
        {
            SchemaVersion = 15,
            WallpaperCaptureSource = "DesktopHost",
            WallpaperHostCaptureMethod = "LegacyWindow",
            WallpaperCaptureRefreshIntervalSeconds = 3601
        };
        (AppSettings normalizedMaximum, _) =
            new SettingsMigrationPipeline().RunMigrationsOnCopy(validAndHigh);
        Assert.Equal("DesktopHost", normalizedMaximum.WallpaperCaptureSource);
        Assert.Equal("LegacyWindow", normalizedMaximum.WallpaperHostCaptureMethod);
        Assert.Equal(3600, normalizedMaximum.WallpaperCaptureRefreshIntervalSeconds);
    }

    [Fact]
    public async Task AppearanceCoordinator_PersistsCaptureChoicesAndClampsInterval()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var coordinator = new AppearanceSettingsCoordinator(settings);

        coordinator.SetWallpaperCaptureSource("SystemWallpaper");
        coordinator.SetWallpaperHostCaptureMethod("FullWindow");
        coordinator.SetWallpaperCaptureRefreshIntervalSeconds(0);
        Assert.Equal(
            new AppearanceWallpaperCaptureSettings("SystemWallpaper", "FullWindow", 1),
            coordinator.ReadWallpaperCapture());
        coordinator.SetWallpaperCaptureRefreshIntervalSeconds(5);
        Assert.Equal(
            new AppearanceWallpaperCaptureSettings("SystemWallpaper", "FullWindow", 5),
            coordinator.ReadWallpaperCapture());

        coordinator.SetWallpaperCaptureSource("DesktopHost");
        coordinator.SetWallpaperHostCaptureMethod("LegacyWindow");
        coordinator.SetWallpaperCaptureRefreshIntervalSeconds(120);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(17, reloaded.Settings.SchemaVersion);
        Assert.Equal("DesktopHost", reloaded.Settings.WidgetShell.WallpaperCaptureSource);
        Assert.Equal("LegacyWindow", reloaded.Settings.WidgetShell.WallpaperHostCaptureMethod);
        Assert.Equal(120, reloaded.Settings.WidgetShell.WallpaperCaptureRefreshIntervalSeconds);

        using JsonDocument persisted = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        JsonElement root = persisted.RootElement;
        Assert.Equal(17, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("DesktopHost", root.GetProperty("wallpaperCaptureSource").GetString());
        Assert.Equal("LegacyWindow", root.GetProperty("wallpaperHostCaptureMethod").GetString());
        Assert.Equal(120, root.GetProperty("wallpaperCaptureRefreshIntervalSeconds").GetInt32());
    }
}
