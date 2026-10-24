using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private readonly Dictionary<FileItemSurface, AdaptiveNameSampleState> _adaptiveNameChoices =
        new(ReferenceEqualityComparer.Instance);
    private DispatcherTimer? _adaptiveContrastTimer;
    private IDisposable? _wallpaperContrastLease;
    private AccessibilitySettings? _contrastAccessibilitySettings;
    private bool _isContrastSurfaceLoaded;
    private bool _adaptiveContrastSamplePending;

    internal void RefreshAdaptiveFilenameContrast() =>
        UpdateAdaptiveFilenameContrast();

    private void FileSurfaceContent_SettingsChanged() =>
        UpdateAdaptiveFilenameContrast();

    private void UpdateAdaptiveFilenameContrast()
    {
        if (_isDisposed) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateAdaptiveFilenameContrast);
            return;
        }

        bool isAutomatic = WidgetForegroundSettings.ResolveMode(
            Config,
            _settingsService.Settings) == WidgetForegroundSettings.ModeAutoBackground;
        if (isAutomatic && _isContrastSurfaceLoaded && _isWindowVisible)
        {
            EnsureContrastAccessibilityWatcher();
        }
        else
        {
            DisposeContrastAccessibilityWatcher();
        }

        bool shouldSample = isAutomatic &&
            _isContrastSurfaceLoaded &&
            _isWindowVisible &&
            _hostWindowHandle != IntPtr.Zero &&
            _contrastAccessibilitySettings?.HighContrast != true &&
            !WindowsCompatibilityService.IsHighContrast;
        if (!shouldSample)
        {
            StopAdaptiveFilenameContrast(clearLabels: true);
            return;
        }

        _wallpaperContrastLease ??= WallpaperContrastService.Acquire();
        if (_adaptiveContrastTimer is null)
        {
            _adaptiveContrastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _adaptiveContrastTimer.Tick += AdaptiveContrastTimer_Tick;
        }

        if (!_adaptiveContrastTimer.IsEnabled)
        {
            _adaptiveContrastTimer.Start();
        }

        SampleAdaptiveFilenameContrast();
    }

    private void AdaptiveContrastTimer_Tick(object? sender, object e)
    {
        _adaptiveContrastSamplePending = false;
        SampleAdaptiveFilenameContrast();
    }

    private void QueueAdaptiveFilenameContrastSample()
    {
        if (_adaptiveContrastTimer?.IsEnabled == true)
        {
            // Container realization and recycling can raise many load and
            // DataContext events in one turn. Defer the scan to the shared
            // 150ms geometry timer so those events collapse into one pass.
            _adaptiveContrastSamplePending = true;
        }
    }

    private void SampleAdaptiveFilenameContrast()
    {
        if (_isDisposed ||
            !_isContrastSurfaceLoaded ||
            !_isWindowVisible ||
            _hostWindowHandle == IntPtr.Zero ||
            WindowsCompatibilityService.IsHighContrast)
        {
            ResetAdaptiveFilenameLabels();
            return;
        }

        AppSettings settings = _settingsService.Settings;
        if (WidgetForegroundSettings.ResolveMode(Config, settings) !=
            WidgetForegroundSettings.ModeAutoBackground)
        {
            ResetAdaptiveFilenameLabels();
            return;
        }

        bool hasSolidWidgetBackground = false;
        double luminanceOverlayOpacity = 0;
        Color luminanceOverlayColor = default;
        if (WidgetBackgroundCustomization.GetImageFileNameOverride(Config) is not null)
        {
            ResetAdaptiveFilenameLabels();
            return;
        }

        if (WidgetBackgroundCustomization.GetColorOverride(Config) is { } solidHex &&
            AccentColorHelper.TryParseHex(solidHex, out Color widgetBackgroundColor))
        {
            hasSolidWidgetBackground = true;
            luminanceOverlayColor = widgetBackgroundColor;
            luminanceOverlayOpacity = 1;
        }
        else if (WidgetBackgroundCustomization.IsGlobalImageBackgroundActive(settings))
        {
            // Custom image backdrops are not represented by the desktop map.
            ResetAdaptiveFilenameLabels();
            return;
        }
        else if (WindowsCompatibilityService.ResolveWidgetMaterialType(
                     settings.WidgetMaterialType) == SettingsService.WidgetMaterialTypeSolid)
        {
            var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor;
            luminanceOverlayColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                Root.ActualTheme == ElementTheme.Dark,
                accent,
                settings.WidgetOpacity);
            luminanceOverlayOpacity = luminanceOverlayColor.A / 255d;
            hasSolidWidgetBackground = true;
        }
        else
        {
            // Acrylic and Mica do not expose a stable, known color overlay.
            ResetAdaptiveFilenameLabels();
            return;
        }

        foreach (Border border in _itemSurfaces.ToArray())
        {
            FileItemSurface? surface = FileItemSurface.FindOwner(border);
            if (surface is null ||
                surface.DataContext is not WidgetItem item ||
                item is WidgetStackItem ||
                !ReferenceEquals(surface.XamlRoot, Root.XamlRoot) ||
                surface.Visibility != Visibility.Visible)
            {
                if (surface is not null) ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            TextBlock name = surface.ItemNameText;
            if (name.Visibility != Visibility.Visible ||
                !TryGetVisiblePhysicalBounds(name, FileItemsViewport, out WallpaperContrastRect screenBounds))
            {
                ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            double wallpaperLuminance = 0;
            bool wallpaperSampleNeeded = !hasSolidWidgetBackground || luminanceOverlayOpacity < 1;
            long wallpaperVersion = wallpaperSampleNeeded
                ? WallpaperContrastService.SnapshotVersion
                : 0;
            if (_adaptiveNameChoices.TryGetValue(surface, out AdaptiveNameSampleState cached) &&
                cached.Bounds == screenBounds &&
                ReferenceEquals(cached.Label, name) &&
                cached.WallpaperVersion == wallpaperVersion &&
                cached.HasSolidWidgetBackground == hasSolidWidgetBackground &&
                cached.OverlayColor.Equals(luminanceOverlayColor) &&
                cached.OverlayOpacity.Equals(luminanceOverlayOpacity))
            {
                continue;
            }

            bool hasWallpaperSample = wallpaperSampleNeeded &&
                WallpaperContrastService.TrySampleLuminance(screenBounds, out wallpaperLuminance);
            if (wallpaperSampleNeeded && !hasWallpaperSample)
            {
                ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            double backgroundLuminance;
            if (luminanceOverlayOpacity >= 1)
            {
                backgroundLuminance = WallpaperContrastPolicy.SrgbLuminance(
                    luminanceOverlayColor.R,
                    luminanceOverlayColor.G,
                    luminanceOverlayColor.B);
            }
            else
            {
                backgroundLuminance = hasSolidWidgetBackground
                    ? WallpaperContrastPolicy.BlendLuminance(
                        wallpaperLuminance,
                        luminanceOverlayColor.R,
                        luminanceOverlayColor.G,
                        luminanceOverlayColor.B,
                        luminanceOverlayOpacity)
                    : wallpaperLuminance;
            }

            bool? previousWhite = _adaptiveNameChoices.TryGetValue(
                surface,
                out AdaptiveNameSampleState previous)
                    ? previous.PreferWhite
                    : null;
            bool preferWhite = WallpaperContrastPolicy.PreferWhite(
                backgroundLuminance,
                previousWhite);
            surface.SetAdaptiveNameForeground(preferWhite);
            _adaptiveNameChoices[surface] = new AdaptiveNameSampleState(
                screenBounds,
                name,
                wallpaperVersion,
                hasSolidWidgetBackground,
                luminanceOverlayColor,
                luminanceOverlayOpacity,
                preferWhite);
        }
    }

    private bool TryGetVisiblePhysicalBounds(
        FrameworkElement element,
        FrameworkElement viewport,
        out WallpaperContrastRect screenBounds)
    {
        screenBounds = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Rect localBounds = new(
                0,
                0,
                element.ActualWidth,
                element.ActualHeight);
            Windows.Foundation.Rect viewportBounds = new(
                0,
                0,
                viewport.ActualWidth,
                viewport.ActualHeight);
            Windows.Foundation.Rect nameInRoot = element.TransformToVisual(null)
                .TransformBounds(localBounds);
            Windows.Foundation.Rect viewportInRoot = viewport.TransformToVisual(null)
                .TransformBounds(viewportBounds);
            Windows.Foundation.Rect visible = Intersect(nameInRoot, viewportInRoot);
            if (visible.Width <= 0 || visible.Height <= 0) return false;

            var clientOrigin = default(Win32Helper.POINT);
            if (!Win32Helper.ClientToScreen(_hostWindowHandle, ref clientOrigin)) return false;
            double scale = Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                viewport.XamlRoot);
            if (!double.IsFinite(scale) || scale <= 0) return false;

            screenBounds = new WallpaperContrastRect(
                clientOrigin.X + (visible.X * scale),
                clientOrigin.Y + (visible.Y * scale),
                clientOrigin.X + (visible.X + visible.Width) * scale,
                clientOrigin.Y + (visible.Y + visible.Height) * scale);
            return screenBounds.Width > 0 && screenBounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Windows.Foundation.Rect Intersect(
        Windows.Foundation.Rect left,
        Windows.Foundation.Rect right)
    {
        double x = Math.Max(left.X, right.X);
        double y = Math.Max(left.Y, right.Y);
        double rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        double bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return rightEdge <= x || bottomEdge <= y
            ? new Windows.Foundation.Rect()
            : new Windows.Foundation.Rect(x, y, rightEdge - x, bottomEdge - y);
    }

    private void EnsureContrastAccessibilityWatcher()
    {
        if (_contrastAccessibilitySettings is not null) return;
        try
        {
            _contrastAccessibilitySettings = new AccessibilitySettings();
            _contrastAccessibilitySettings.HighContrastChanged +=
                ContrastAccessibilitySettings_HighContrastChanged;
        }
        catch
        {
            _contrastAccessibilitySettings = null;
        }
    }

    private void ContrastAccessibilitySettings_HighContrastChanged(
        AccessibilitySettings sender,
        object args)
    {
        if (!DispatcherQueue.TryEnqueue(UpdateAdaptiveFilenameContrast))
        {
            App.LogVerbose("[WallpaperContrast] Could not queue accessibility refresh.");
        }
    }

    private void DisposeContrastAccessibilityWatcher()
    {
        if (_contrastAccessibilitySettings is null) return;
        _contrastAccessibilitySettings.HighContrastChanged -=
            ContrastAccessibilitySettings_HighContrastChanged;
        _contrastAccessibilitySettings = null;
    }

    private void StopAdaptiveFilenameContrast(bool clearLabels)
    {
        _adaptiveContrastSamplePending = false;
        if (_adaptiveContrastTimer is not null)
        {
            _adaptiveContrastTimer.Stop();
            _adaptiveContrastTimer.Tick -= AdaptiveContrastTimer_Tick;
            _adaptiveContrastTimer = null;
        }

        _wallpaperContrastLease?.Dispose();
        _wallpaperContrastLease = null;
        if (clearLabels) ResetAdaptiveFilenameLabels();
    }

    private void ResetAdaptiveFilenameLabels()
    {
        foreach (FileItemSurface surface in _adaptiveNameChoices.Keys.ToArray())
        {
            surface.SetAdaptiveNameForeground(null);
        }
        _adaptiveNameChoices.Clear();
    }

    private void ClearAdaptiveFilenameChoice(FileItemSurface surface)
    {
        if (_adaptiveNameChoices.Remove(surface))
        {
            surface.SetAdaptiveNameForeground(null);
        }
    }

    private readonly record struct AdaptiveNameSampleState(
        WallpaperContrastRect Bounds,
        TextBlock Label,
        long WallpaperVersion,
        bool HasSolidWidgetBackground,
        Color OverlayColor,
        double OverlayOpacity,
        bool PreferWhite);
}
