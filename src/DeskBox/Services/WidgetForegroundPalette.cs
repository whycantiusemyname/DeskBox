using DeskBox.Helpers;
using DeskBox.Models;
using Windows.UI;

namespace DeskBox.Services;

/// <summary>
/// Semantic widget foreground tones. The palette is applied through the
/// existing scoped brushes (header and body) so text, hover/pressed states and
/// disabled controls all follow the same source color.
/// </summary>
internal readonly record struct WidgetForegroundPalette(
    Color Primary,
    Color Secondary,
    Color Tertiary,
    Color Disabled,
    Color Divider)
{
    private static readonly Color AutoWhite = Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7);
    private static readonly Color AutoDark = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

    public static WidgetForegroundPalette FromPrimary(Color primary) => new(
        primary,
        WithAlpha(primary, 0xE8),
        WithAlpha(primary, 0xCC),
        WithAlpha(primary, 0x8F),
        WithAlpha(primary, 0x66));

    public static WidgetForegroundPalette FromPreferWhite(bool preferWhite) =>
        FromPrimary(preferWhite ? AutoWhite : AutoDark);

    public static WidgetForegroundPalette FromSolid(Color color) =>
        new(color, color, color, color, color);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}

internal enum WidgetAutoBackgroundSupport
{
    Supported,
    WidgetImageOverride,
    GlobalImageBackground,
    UnsupportedMaterial
}

internal readonly record struct WidgetAutoBackgroundSurface(
    WidgetAutoBackgroundSupport Support,
    bool HasSolidWidgetBackground,
    Color OverlayColor,
    double OverlayOpacity)
{
    public bool IsSupported => Support == WidgetAutoBackgroundSupport.Supported;
}

/// <summary>
/// Resolves the part of the widget surface that covers the desktop wallpaper.
/// Only a stable solid overlay can be blended with a wallpaper sample; user
/// images and Acrylic/Mica intentionally fall back to the theme palette.
/// </summary>
internal static class WidgetAutoBackgroundContrastPolicy
{
    public static bool TryResolveSurface(
        WidgetConfig config,
        AppSettings settings,
        bool isDark,
        Color? effectiveAccentColor,
        out WidgetAutoBackgroundSurface surface)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);

        EffectiveWidgetBackgroundLayer layer =
            WidgetBackgroundCustomization.ResolveEffectiveLayer(config, settings);
        if (layer == EffectiveWidgetBackgroundLayer.WidgetImageOverride)
        {
            surface = new WidgetAutoBackgroundSurface(
                WidgetAutoBackgroundSupport.WidgetImageOverride,
                HasSolidWidgetBackground: false,
                OverlayColor: default,
                OverlayOpacity: 0);
            return false;
        }

        if (layer == EffectiveWidgetBackgroundLayer.GlobalImage)
        {
            surface = new WidgetAutoBackgroundSurface(
                WidgetAutoBackgroundSupport.GlobalImageBackground,
                HasSolidWidgetBackground: false,
                OverlayColor: default,
                OverlayOpacity: 0);
            return false;
        }

        if (layer == EffectiveWidgetBackgroundLayer.PerWidgetColor &&
            WidgetBackgroundCustomization.GetColorOverride(config) is { } solidHex &&
            AccentColorHelper.TryParseHex(solidHex, out Color widgetBackgroundColor))
        {
            surface = new WidgetAutoBackgroundSurface(
                WidgetAutoBackgroundSupport.Supported,
                HasSolidWidgetBackground: true,
                OverlayColor: widgetBackgroundColor,
                OverlayOpacity: 1);
            return true;
        }

        if (WindowsCompatibilityService.ResolveWidgetMaterialType(
                settings.WidgetShell.WidgetMaterialType) == SettingsService.WidgetMaterialTypeSolid)
        {
            Color accent = effectiveAccentColor ?? AccentColorHelper.DefaultAccentColor;
            Color overlayColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accent,
                settings.WidgetShell.WidgetOpacity);
            surface = new WidgetAutoBackgroundSurface(
                WidgetAutoBackgroundSupport.Supported,
                HasSolidWidgetBackground: true,
                OverlayColor: overlayColor,
                OverlayOpacity: overlayColor.A / 255d);
            return true;
        }

        surface = new WidgetAutoBackgroundSurface(
            WidgetAutoBackgroundSupport.UnsupportedMaterial,
            HasSolidWidgetBackground: false,
            OverlayColor: default,
            OverlayOpacity: 0);
        return false;
    }

    public static double ResolveBackgroundLuminance(
        double wallpaperLuminance,
        in WidgetAutoBackgroundSurface surface)
    {
        if (surface.OverlayOpacity >= 1)
        {
            return WallpaperContrastPolicy.SrgbLuminance(
                surface.OverlayColor.R,
                surface.OverlayColor.G,
                surface.OverlayColor.B);
        }

        return WallpaperContrastPolicy.BlendLuminance(
            wallpaperLuminance,
            surface.OverlayColor.R,
            surface.OverlayColor.G,
            surface.OverlayColor.B,
            surface.OverlayOpacity);
    }
}
