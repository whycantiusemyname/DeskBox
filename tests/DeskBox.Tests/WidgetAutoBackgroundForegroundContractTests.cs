using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class WidgetAutoBackgroundForegroundContractTests
{
    [Fact]
    public void SolidColorOverride_IsAnOpaqueAutomaticContrastSurface()
    {
        var config = new WidgetConfig
        {
            Metadata =
            {
                [WidgetBackgroundCustomization.ColorMetadataKey] = "#336699"
            }
        };

        bool supported = WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
            config,
            new AppSettings(),
            isDark: false,
            effectiveAccentColor: null,
            out WidgetAutoBackgroundSurface surface);

        Assert.True(supported);
        Assert.Equal(WidgetAutoBackgroundSupport.Supported, surface.Support);
        Assert.True(surface.HasSolidWidgetBackground);
        Assert.Equal(1d, surface.OverlayOpacity);
        Assert.Equal(Color.FromArgb(0xFF, 0x33, 0x66, 0x99), surface.OverlayColor);
    }

    [Fact]
    public void WidgetImageOverride_DoesNotSampleTheWallpaper()
    {
        var config = new WidgetConfig
        {
            Metadata =
            {
                [WidgetBackgroundCustomization.ImageMetadataKey] = "background.png"
            }
        };

        bool supported = WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
            config,
            new AppSettings(),
            isDark: false,
            effectiveAccentColor: null,
            out WidgetAutoBackgroundSurface surface);

        Assert.False(supported);
        Assert.Equal(WidgetAutoBackgroundSupport.WidgetImageOverride, surface.Support);
        Assert.False(surface.HasSolidWidgetBackground);
    }

    [Fact]
    public void AcrylicMaterial_FallsBackToTheThemePalette()
    {
        var settings = new AppSettings
        {
            WidgetMaterialType = SettingsService.WidgetMaterialTypeAcrylic
        };

        bool supported = WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
            new WidgetConfig(),
            settings,
            isDark: true,
            effectiveAccentColor: Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
            out WidgetAutoBackgroundSurface surface);

        Assert.False(supported);
        Assert.Equal(WidgetAutoBackgroundSupport.UnsupportedMaterial, surface.Support);
        Assert.False(surface.IsSupported);
    }

    [Fact]
    public void SolidMaterial_BlendsItsOpacityIntoTheSampledLuminance()
    {
        var settings = new AppSettings
        {
            WidgetMaterialType = SettingsService.WidgetMaterialTypeSolid,
            WidgetOpacity = 0.5
        };

        bool supported = WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
            new WidgetConfig(),
            settings,
            isDark: false,
            effectiveAccentColor: Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
            out WidgetAutoBackgroundSurface surface);

        Assert.True(supported);
        Assert.Equal(WidgetAutoBackgroundSupport.Supported, surface.Support);
        Assert.True(surface.HasSolidWidgetBackground);
        Assert.Equal(surface.OverlayColor.A / 255d, surface.OverlayOpacity, 3);

        double whiteWallpaper = WidgetAutoBackgroundContrastPolicy.ResolveBackgroundLuminance(
            1,
            surface);
        double blackWallpaper = WidgetAutoBackgroundContrastPolicy.ResolveBackgroundLuminance(
            0,
            surface);
        Assert.True(whiteWallpaper > blackWallpaper);
    }

    [Fact]
    public void Palette_KeepsReadableHoverPressedAndDisabledTonesTogether()
    {
        WidgetForegroundPalette white = WidgetForegroundPalette.FromPreferWhite(true);
        WidgetForegroundPalette dark = WidgetForegroundPalette.FromPreferWhite(false);

        Assert.Equal(Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7), white.Primary);
        Assert.Equal(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A), dark.Primary);
        Assert.Equal((byte)0xE8, white.Secondary.A);
        Assert.Equal((byte)0xCC, white.Tertiary.A);
        Assert.Equal((byte)0x8F, white.Disabled.A);
        Assert.Equal((byte)0x66, white.Divider.A);
        Assert.Equal(white.Secondary, WidgetForegroundPalette.FromPrimary(white.Primary).Secondary);

        Color system = Color.FromArgb(0xFF, 0x00, 0xFF, 0x00);
        WidgetForegroundPalette highContrast = WidgetForegroundPalette.FromSolid(system);
        Assert.Equal(system, highContrast.Primary);
        Assert.Equal(system, highContrast.Secondary);
        Assert.Equal(system, highContrast.Disabled);
    }

    [Fact]
    public void WindowContrast_SamplesHeaderAndBodyScopesAndStopsItsOwnResources()
    {
        string foreground = Read("src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml");

        Assert.Contains("WallpaperContrastService.Acquire()", foreground, StringComparison.Ordinal);
        Assert.Contains("WallpaperContrastService.ConfigureCaptureOptions", foreground, StringComparison.Ordinal);
        Assert.Contains("WallpaperContrastService.TrySampleLuminance", foreground, StringComparison.Ordinal);
        Assert.Contains("ForegroundHeaderSampleElement", foreground, StringComparison.Ordinal);
        Assert.Contains("ForegroundBodySampleElement", foreground, StringComparison.Ordinal);
        Assert.Contains("StopAutoForegroundSampling(clearState: true);", foreground, StringComparison.Ordinal);

        Assert.Contains("WidgetHeaderPrimaryBrush", shell, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                shell,
                "x:Key=\"TextFillColorPrimaryBrush\" ResourceKey=\"WidgetHeaderPrimaryBrush\""));
        Assert.Contains(
            "Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdChangesInvalidateBothCachesAndResetTheirHysteresisChoice()
    {
        string foreground = Read("src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string fileNames = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Contrast.cs");

        Assert.Contains("cachedThresholdPercent: previous?.ThresholdPercent", foreground, StringComparison.Ordinal);
        Assert.Contains("header.ThresholdPercent.Equals(thresholdPercent)", foreground, StringComparison.Ordinal);
        Assert.Contains("body.ThresholdPercent.Equals(thresholdPercent)", foreground, StringComparison.Ordinal);
        Assert.Contains("previousSample.ThresholdPercent.Equals(thresholdPercent)", foreground, StringComparison.Ordinal);
        Assert.Contains("cachedThresholdPercent: cached.ThresholdPercent", fileNames, StringComparison.Ordinal);
        Assert.Contains("previous.ThresholdPercent.Equals(thresholdPercent)", fileNames, StringComparison.Ordinal);
        Assert.Contains("threshold: threshold", fileNames, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactChrome_InvalidatesTheHeaderAndBodySamples()
    {
        string navigation = Read("src/DeskBox/Views/ContentWidgetWindow.GroupNavigation.cs");
        string baseWindow = Read("src/DeskBox/Views/WidgetWindowBase.cs");

        Assert.Contains(
            "base.OnCompactVisualStateChanged(collapsed);",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "InvalidateAutoForegroundSamples();",
            baseWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueAutoForegroundAppearanceRefresh();",
            baseWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompactHint_OpaqueSurfaceDoesNotUseTheAdaptiveHeaderBrush()
    {
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml");
        int hintStart = shell.IndexOf(
            "x:Name=\"CompactHintPresenter\"",
            StringComparison.Ordinal);
        int hintEnd = shell.IndexOf(
            "x:Name=\"CompactTextContainer\"",
            hintStart,
            StringComparison.Ordinal);
        Assert.True(hintStart >= 0 && hintEnd > hintStart);
        string hint = shell[hintStart..hintEnd];

        Assert.Contains("WidgetCompactHintForegroundBrush", hint, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrast\"", hint, StringComparison.Ordinal);
        Assert.Contains("SystemColorWindowTextColor", hint, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"",
            hint,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FullBleedCompactPresentation_GatesDesktopSamplingAndUsesItsOwnTheme()
    {
        string shellCode = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string foreground = Read("src/DeskBox/Views/WidgetWindowBase.Foreground.cs");

        Assert.Contains(
            "CompactPresentationOwnsForegroundContract",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactPresentationForegroundTheme",
            shellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactForegroundContractChanged",
            shellCode,
            StringComparison.Ordinal);

        Assert.Contains(
            "!compactPresentationOwnsForeground",
            foreground,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetShellControl.CompactPresentationForegroundTheme",
            foreground,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetShellControl.CompactPresentationOwnsForegroundContract",
            foreground,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizedAutomaticContrast_CoversTitlesControlsAndFileNames()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] files = Directory.GetFiles(stringsRoot, "*.json");
        Assert.Equal(13, files.Length);

        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            string? label = root.GetProperty(
                "Settings.WidgetForeground.AutoBackground").GetString();
            string? description = root.GetProperty(
                "Settings.WidgetForeground.Description").GetString();
            string? sourceDescription = root.GetProperty(
                "Settings.WallpaperCapture.Source.Description").GetString();
            string? thresholdLabel = root.GetProperty(
                "Settings.WidgetForeground.Threshold.Title").GetString();
            string? thresholdDescription = root.GetProperty(
                "Settings.WidgetForeground.Threshold.Description").GetString();

            Assert.False(string.IsNullOrWhiteSpace(label), file);
            Assert.False(string.IsNullOrWhiteSpace(description), file);
            Assert.False(string.IsNullOrWhiteSpace(sourceDescription), file);
            Assert.False(string.IsNullOrWhiteSpace(thresholdLabel), file);
            Assert.False(string.IsNullOrWhiteSpace(thresholdDescription), file);
            Assert.DoesNotContain("Automatic filename contrast", label, StringComparison.Ordinal);
            Assert.DoesNotContain("automatic filename contrast", sourceDescription, StringComparison.Ordinal);

            // Capture settings remain present in every locale.
            Assert.True(root.TryGetProperty(
                "Settings.WallpaperCapture.Source.Auto", out _), file);
            Assert.True(root.TryGetProperty(
                "Settings.WallpaperCapture.Method.Description", out _), file);
            Assert.True(root.TryGetProperty(
                "Settings.WallpaperCapture.RefreshInterval.Description", out _), file);
        }

        using JsonDocument english = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(stringsRoot, "en-US.json")));
        JsonElement englishRoot = english.RootElement;
        Assert.Equal(
            "Automatic background contrast",
            englishRoot.GetProperty("Settings.WidgetForeground.AutoBackground").GetString());
        string englishDescription = englishRoot.GetProperty(
            "Settings.WidgetForeground.Description").GetString()!;
        Assert.Contains("titles", englishDescription, StringComparison.Ordinal);
        Assert.Contains("monochrome controls", englishDescription, StringComparison.Ordinal);
        Assert.Contains("file names", englishDescription, StringComparison.Ordinal);
        string englishThresholdDescription = englishRoot.GetProperty(
            "Settings.WidgetForeground.Threshold.Description").GetString()!;
        Assert.Contains("higher values", englishThresholdDescription, StringComparison.Ordinal);
        Assert.Contains("linear", englishThresholdDescription, StringComparison.Ordinal);
        Assert.Contains("not screen brightness", englishThresholdDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableUpdate_PackagesThePriAndSignsEveryApplicationFile()
    {
        string workflow = Read(".github/workflows/ci.yml");

        Assert.Contains("'DeskBox.exe'", workflow, StringComparison.Ordinal);
        Assert.Contains("'DeskBox.pri'", workflow, StringComparison.Ordinal);
        Assert.Contains("'Assets'", workflow, StringComparison.Ordinal);
        Assert.Contains("'ThirdParty'", workflow, StringComparison.Ordinal);
        Assert.Contains("compatibleRuntimeVersion = '2.5.1'", workflow, StringComparison.Ordinal);
        Assert.Contains("exeSha256 =", workflow, StringComparison.Ordinal);
        Assert.Contains("$applicationPayload", workflow, StringComparison.Ordinal);
        Assert.Contains("path = $relativePath", workflow, StringComparison.Ordinal);
        Assert.Contains("sha256 = (Get-FileHash", workflow, StringComparison.Ordinal);
        Assert.Contains("files = @($payloadFiles", workflow, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
