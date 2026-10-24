using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private AccessibilitySettings? _foregroundAccessibilitySettings;
    private DispatcherQueueTimer? _autoForegroundTimer;
    private IDisposable? _autoForegroundLease;
    private AutoForegroundSampleState? _headerAutoSample;
    private AutoForegroundSampleState? _bodyAutoSample;
    private string? _lastAutoCaptureSignature;
    private bool _autoForegroundRefreshQueued;

    protected void ApplyWidgetForegroundAppearance()
    {
        EnsureForegroundAccessibilityWatcher();
        bool highContrast = _foregroundAccessibilitySettings?.HighContrast == true;
        string mode = WidgetForegroundSettings.ResolveMode(
            Config,
            SettingsService.Settings);
        Color customColor = WidgetForegroundSettings.ResolveCustomColor(
            Config,
            SettingsService.Settings);
        WidgetForegroundPalette basePalette = ResolveForegroundPalette(
            mode,
            customColor,
            RootElement.ActualTheme,
            highContrast);

        ApplyForegroundBrushes(basePalette);
        WidgetShellControl.ApplyHeaderForegroundPalette(basePalette);
        WidgetShellControl.SetGroupTitleForegroundColors(
            basePalette.Primary,
            basePalette.Secondary,
            basePalette.Disabled,
            highContrast);

        bool automatic = mode == WidgetForegroundSettings.ModeAutoBackground &&
            !highContrast;
        if (automatic && TryResolveAutoBackgroundSurface(out WidgetAutoBackgroundSurface surface))
        {
            ConfigureAutoForegroundCaptureOptions();
            if (surface.OverlayOpacity >= 1)
            {
                StopAutoForegroundSampling(clearState: true);
                ApplyUniformAutoBackgroundPalette(surface);
            }
            else if (CanSampleAutoForeground())
            {
                StartAutoForegroundSampling();
                ReapplyCachedAutoBackgroundPalettes();
                SampleAutoForegroundContrast(surface);
            }
            else
            {
                StopAutoForegroundSampling(clearState: true);
            }
        }
        else
        {
            StopAutoForegroundSampling(clearState: true);
        }

        OnWidgetForegroundAppearanceApplied();
    }

    protected virtual void OnWidgetForegroundAppearanceApplied() { }

    protected void SetWidgetForegroundModeOverride(string? mode)
    {
        WidgetForegroundSettings.SetModeOverride(Config, mode);
        SettingsService.UpdateWidget(Config);
        ApplyWidgetForegroundAppearance();
    }

    /// <summary>
    /// Builds the custom-foreground-color picker as an anchored flyout. The
    /// window decides where to show it; a ContentDialog inside a small widget
    /// window gets clipped by the window bounds, while a flyout follows the
    /// same escape-the-window placement the widget context menus use.
    /// </summary>
    protected Flyout BuildWidgetForegroundColorPickerFlyout()
    {
        var picker = new ColorPicker
        {
            Color = WidgetForegroundSettings.ResolveCustomColor(
                Config,
                SettingsService.Settings),
            IsAlphaEnabled = false,
            MinWidth = 256
        };
        var localization = App.Current.LocalizationService;
        var saveButton = new Button
        {
            Content = localization.T("Common.Save"),
            MinWidth = 96
        };
        var cancelButton = new Button
        {
            Content = localization.T("Common.Cancel"),
            MinWidth = 96
        };
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = localization.T("Widget.Foreground.CustomColor"),
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = localization.T("Widget.Foreground.ContrastHint"),
                        FontSize = 11,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
                    },
                    picker,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, saveButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => flyout.Hide();
        saveButton.Click += (_, _) =>
        {
            try
            {
                WidgetForegroundSettings.SetCustomColorOverride(Config, picker.Color);
                WidgetForegroundSettings.SetModeOverride(
                    Config,
                    WidgetForegroundSettings.ModeCustom);
                SettingsService.UpdateWidget(Config);
                ApplyWidgetForegroundAppearance();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetForeground] Color picker failed: {ex.Message}");
            }
            finally
            {
                flyout.Hide();
            }
        };
        return flyout;
    }

    private void EnsureForegroundAccessibilityWatcher()
    {
        if (_foregroundAccessibilitySettings is not null)
        {
            return;
        }

        try
        {
            _foregroundAccessibilitySettings = new AccessibilitySettings();
            _foregroundAccessibilitySettings.HighContrastChanged +=
                ForegroundAccessibilitySettings_HighContrastChanged;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[WidgetForeground] Accessibility watcher unavailable: {ex.Message}");
        }
    }

    private void ForegroundAccessibilitySettings_HighContrastChanged(
        AccessibilitySettings sender,
        object args)
    {
        if (!DispatcherQueue.TryEnqueue(ApplyWidgetForegroundAppearance))
        {
            App.LogVerbose("[WidgetForeground] Could not queue high-contrast refresh.");
        }
    }

    private void CleanupWidgetForegroundAppearance()
    {
        StopAutoForegroundSampling(clearState: true);
        if (_foregroundAccessibilitySettings is not null)
        {
            _foregroundAccessibilitySettings.HighContrastChanged -=
                ForegroundAccessibilitySettings_HighContrastChanged;
            _foregroundAccessibilitySettings = null;
        }
    }

    private void ApplyForegroundBrushes(WidgetForegroundPalette palette)
    {
        // The drag handle renders over widget content without a plate behind
        // it, so a translucent tone shows the content through the bar. It
        // follows the full-opacity primary tone instead of the tertiary one.
        SetBrushColor(palette.Primary,
            "TextFillColorPrimaryBrush",
            "ControlStrongFillColorDefaultBrush",
            "ButtonForeground",
            "ButtonForegroundPointerOver",
            "SubtleButtonForeground",
            "SubtleButtonForegroundPointerOver",
            "WidgetDragHandleBrush");
        SetBrushColor(palette.Secondary,
            "TextFillColorSecondaryBrush",
            "ControlStrongStrokeColorDefaultBrush",
            "ButtonForegroundPressed",
            "SubtleButtonForegroundPressed");
        SetBrushColor(palette.Tertiary,
            "TextFillColorTertiaryBrush");
        SetBrushColor(palette.Disabled,
            "TextFillColorDisabledBrush",
            "ControlStrongFillColorDisabledBrush",
            "ControlStrongStrokeColorDisabledBrush",
            "ButtonForegroundDisabled",
            "SubtleButtonForegroundDisabled");
        SetBrushColor(palette.Divider, "DividerStrokeColorDefaultBrush");
    }

    private void SetBrushColor(Color color, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (RootElement.Resources.TryGetValue(key, out object? value) &&
                value is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static WidgetForegroundPalette ResolveForegroundPalette(
        string mode,
        Color customColor,
        ElementTheme actualTheme,
        bool highContrast)
    {
        if (highContrast)
        {
            // Reuse the shared cached UISettings instance instead of a new
            // projection per palette resolve; TryGetUiColor already carries
            // the unavailability fallback contract.
            Color? foreground = WindowsCompatibilityService.TryGetUiColor(
                UIColorType.Foreground);
            if (foreground is { } highContrastForeground)
            {
                // High contrast uses the system foreground everywhere; no
                // decorative alpha ramp may reduce the system-owned contrast.
                return WidgetForegroundPalette.FromSolid(highContrastForeground);
            }

            // Continue with the selected palette if WinRT accessibility
            // colors are temporarily unavailable.
        }

        Color primary = mode switch
        {
            WidgetForegroundSettings.ModeLight =>
                Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7),
            WidgetForegroundSettings.ModeDark =>
                Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            WidgetForegroundSettings.ModeCustom =>
                Color.FromArgb(0xFF, customColor.R, customColor.G, customColor.B),
            // AutoBackground resolves a header palette and a body palette
            // from the wallpaper. Until a sample is available (or when the
            // material cannot show the wallpaper), the theme palette remains
            // the documented fallback.
            _ when actualTheme == ElementTheme.Light =>
                Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            _ => Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7)
        };

        return WidgetForegroundPalette.FromPrimary(primary);
    }

    private bool TryResolveAutoBackgroundSurface(
        out WidgetAutoBackgroundSurface surface)
    {
        Color? accent = App.Current?.ThemeService?.GetEffectiveAccentColor();
        return WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
            Config,
            SettingsService.Settings,
            RootElement.ActualTheme == ElementTheme.Dark,
            accent,
            out surface);
    }

    private bool CanSampleAutoForeground() =>
        !IsClosing &&
        RootElement.IsLoaded &&
        HWnd != IntPtr.Zero &&
        Win32Helper.IsWindowVisible(HWnd) &&
        !WindowsCompatibilityService.IsHighContrast;

    private void ConfigureAutoForegroundCaptureOptions()
    {
        WidgetShellSettingsSlice captureSettings = SettingsService.Settings.WidgetShell;
        WallpaperContrastSource source =
            WallpaperCapturePreferenceKinds.NormalizeSource(
                captureSettings.WallpaperCaptureSource) switch
            {
                WallpaperCapturePreferenceKinds.SourceDesktopHost =>
                    WallpaperContrastSource.DesktopHost,
                WallpaperCapturePreferenceKinds.SourceSystemWallpaper =>
                    WallpaperContrastSource.SystemWallpaper,
                _ => WallpaperContrastSource.Auto
            };
        WallpaperHostCaptureMethod method =
            WallpaperCapturePreferenceKinds.NormalizeMethod(
                captureSettings.WallpaperHostCaptureMethod) switch
            {
                WallpaperCapturePreferenceKinds.MethodFullWindow =>
                    WallpaperHostCaptureMethod.FullWindow,
                WallpaperCapturePreferenceKinds.MethodLegacyWindow =>
                    WallpaperHostCaptureMethod.LegacyWindow,
                _ => WallpaperHostCaptureMethod.Auto
            };
        int refreshIntervalSeconds =
            WallpaperCapturePreferenceKinds.NormalizeRefreshIntervalSeconds(
                captureSettings.WallpaperCaptureRefreshIntervalSeconds);
        WallpaperContrastService.ConfigureCaptureOptions(new WallpaperCaptureOptions(
            source,
            method,
            refreshIntervalSeconds));

        string signature = $"{source}|{method}|{refreshIntervalSeconds}";
        if (!string.Equals(
                signature,
                _lastAutoCaptureSignature,
                StringComparison.Ordinal))
        {
            _lastAutoCaptureSignature = signature;
            // Capture preferences changed the pixel source; old samples are no
            // longer comparable even before the service publishes a new one.
            InvalidateAutoForegroundSamples();
        }
    }

    private void StartAutoForegroundSampling()
    {
        _autoForegroundLease ??= WallpaperContrastService.Acquire();
        if (_autoForegroundTimer is null)
        {
            _autoForegroundTimer = DispatcherQueue.CreateTimer();
            _autoForegroundTimer.Interval = TimeSpan.FromMilliseconds(150);
            _autoForegroundTimer.Tick += AutoForegroundTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        if (!_autoForegroundTimer.IsRunning)
        {
            _autoForegroundTimer.Start();
        }
    }

    private void AutoForegroundTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (IsClosing)
        {
            StopAutoForegroundSampling(clearState: true);
            return;
        }

        if (WidgetForegroundSettings.ResolveMode(
                Config,
                SettingsService.Settings) != WidgetForegroundSettings.ModeAutoBackground ||
            !CanSampleAutoForeground() ||
            !TryResolveAutoBackgroundSurface(out WidgetAutoBackgroundSurface surface) ||
            surface.OverlayOpacity >= 1)
        {
            // A gate changed without a settings/visibility callback reaching
            // this window (for example a monitor material fallback). Rebuild
            // the base palette and stop the timer instead of sampling stale
            // geometry forever.
            ApplyWidgetForegroundAppearance();
            return;
        }

        SampleAutoForegroundContrast(surface);
    }

    private void SampleAutoForegroundContrast(WidgetAutoBackgroundSurface surface)
    {
        if (!CanSampleAutoForeground()) return;

        if (TryUpdateAutoForegroundRegion(
                WidgetShellControl.ForegroundHeaderSampleElement,
                surface,
                header: true,
                out WidgetForegroundPalette headerPalette))
        {
            ApplyAutoForegroundPalette(header: true, headerPalette);
        }

        if (TryUpdateAutoForegroundRegion(
                WidgetShellControl.ForegroundBodySampleElement,
                surface,
                header: false,
                out WidgetForegroundPalette bodyPalette))
        {
            ApplyAutoForegroundPalette(header: false, bodyPalette);
        }
    }

    private bool TryUpdateAutoForegroundRegion(
        FrameworkElement element,
        WidgetAutoBackgroundSurface surface,
        bool header,
        out WidgetForegroundPalette palette)
    {
        palette = default;
        if (!TryGetAutoForegroundScreenBounds(
                element,
                out WallpaperContrastRect screenBounds))
        {
            return false;
        }

        long wallpaperVersion = surface.OverlayOpacity < 1
            ? WallpaperContrastService.SnapshotVersion
            : 0;
        AutoForegroundSampleState? previous = header
            ? _headerAutoSample
            : _bodyAutoSample;
        if (previous is { } cached &&
            cached.Bounds == screenBounds &&
            cached.WallpaperVersion == wallpaperVersion &&
            cached.HasSolidWidgetBackground == surface.HasSolidWidgetBackground &&
            cached.OverlayColor.Equals(surface.OverlayColor) &&
            cached.OverlayOpacity.Equals(surface.OverlayOpacity))
        {
            return false;
        }

        double wallpaperLuminance = 0;
        if (surface.OverlayOpacity < 1 &&
            !WallpaperContrastService.TrySampleLuminance(
                screenBounds,
                out wallpaperLuminance))
        {
            return false;
        }

        double backgroundLuminance =
            WidgetAutoBackgroundContrastPolicy.ResolveBackgroundLuminance(
                wallpaperLuminance,
                surface);
        bool preferWhite = WallpaperContrastPolicy.PreferWhite(
            backgroundLuminance,
            previous?.PreferWhite);
        var next = new AutoForegroundSampleState(
            screenBounds,
            wallpaperVersion,
            surface.HasSolidWidgetBackground,
            surface.OverlayColor,
            surface.OverlayOpacity,
            preferWhite);

        if (header)
        {
            _headerAutoSample = next;
        }
        else
        {
            _bodyAutoSample = next;
        }

        palette = WidgetForegroundPalette.FromPreferWhite(preferWhite);
        return true;
    }

    private bool TryGetAutoForegroundScreenBounds(
        FrameworkElement element,
        out WallpaperContrastRect screenBounds)
    {
        screenBounds = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            RootElement.ActualWidth <= 0 || RootElement.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var localBounds = new Windows.Foundation.Rect(
                0,
                0,
                element.ActualWidth,
                element.ActualHeight);
            var viewportBounds = new Windows.Foundation.Rect(
                0,
                0,
                RootElement.ActualWidth,
                RootElement.ActualHeight);
            Windows.Foundation.Rect elementInRoot = element.TransformToVisual(null)
                .TransformBounds(localBounds);
            Windows.Foundation.Rect viewportInRoot = RootElement.TransformToVisual(null)
                .TransformBounds(viewportBounds);
            Windows.Foundation.Rect visible = Intersect(elementInRoot, viewportInRoot);
            if (visible.Width <= 0 || visible.Height <= 0) return false;

            var clientOrigin = default(Win32Helper.POINT);
            if (!Win32Helper.ClientToScreen(HWnd, ref clientOrigin))
            {
                return false;
            }

            double scale = Win32Helper.GetDpiScaleForWindow(
                HWnd,
                RootElement.XamlRoot);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return false;
            }

            screenBounds = new WallpaperContrastRect(
                clientOrigin.X + (visible.X * scale),
                clientOrigin.Y + (visible.Y * scale),
                clientOrigin.X + ((visible.X + visible.Width) * scale),
                clientOrigin.Y + ((visible.Y + visible.Height) * scale));
            return screenBounds.Width > 0 && screenBounds.Height > 0;
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetForeground] Auto contrast bounds unavailable: " +
                $"exception={ex.GetType().Name}/0x{unchecked((uint)ex.HResult):X8}");
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

    private void ReapplyCachedAutoBackgroundPalettes()
    {
        if (_headerAutoSample is { } header)
        {
            ApplyAutoForegroundPalette(
                header: true,
                WidgetForegroundPalette.FromPreferWhite(header.PreferWhite));
        }

        if (_bodyAutoSample is { } body)
        {
            ApplyAutoForegroundPalette(
                header: false,
                WidgetForegroundPalette.FromPreferWhite(body.PreferWhite));
        }
    }

    private void ApplyUniformAutoBackgroundPalette(
        WidgetAutoBackgroundSurface surface)
    {
        double luminance =
            WidgetAutoBackgroundContrastPolicy.ResolveBackgroundLuminance(0, surface);
        bool preferWhite = WallpaperContrastPolicy.PreferWhite(luminance, null);
        WidgetForegroundPalette palette =
            WidgetForegroundPalette.FromPreferWhite(preferWhite);
        ApplyAutoForegroundPalette(header: true, palette);
        ApplyAutoForegroundPalette(header: false, palette);
    }

    private void ApplyAutoForegroundPalette(
        bool header,
        WidgetForegroundPalette palette)
    {
        if (header)
        {
            WidgetShellControl.ApplyHeaderForegroundPalette(palette);
            WidgetShellControl.SetGroupTitleForegroundColors(
                palette.Primary,
                palette.Secondary,
                palette.Disabled,
                highContrast: false);
        }
        else
        {
            ApplyForegroundBrushes(palette);
        }
    }

    private void InvalidateAutoForegroundSamples()
    {
        _headerAutoSample = null;
        _bodyAutoSample = null;
    }

    private void StopAutoForegroundSampling(bool clearState)
    {
        if (_autoForegroundTimer is not null)
        {
            _autoForegroundTimer.Stop();
            _autoForegroundTimer.Tick -= AutoForegroundTimer_Tick;
            _autoForegroundTimer = null;
            PerformanceLogger.RecordTransientUiTimerReleased();
        }

        _autoForegroundLease?.Dispose();
        _autoForegroundLease = null;
        if (clearState)
        {
            InvalidateAutoForegroundSamples();
        }
    }

    private void QueueAutoForegroundAppearanceRefresh()
    {
        if (IsClosing || _autoForegroundRefreshQueued)
        {
            return;
        }

        _autoForegroundRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _autoForegroundRefreshQueued = false;
                if (!IsClosing)
                {
                    ApplyWidgetForegroundAppearance();
                }
            }))
        {
            _autoForegroundRefreshQueued = false;
        }
    }

    private readonly record struct AutoForegroundSampleState(
        WallpaperContrastRect Bounds,
        long WallpaperVersion,
        bool HasSolidWidgetBackground,
        Color OverlayColor,
        double OverlayOpacity,
        bool PreferWhite);
}
