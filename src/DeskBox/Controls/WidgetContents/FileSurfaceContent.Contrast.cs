using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
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
    private string? _lastContrastGateSignature;
    private DateTime _lastContrastGateDiagnosticUtc = DateTime.MinValue;
    private string? _lastContrastPassSignature;
    private DateTime _lastContrastPassDiagnosticUtc = DateTime.MinValue;

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
        string gateReason = !isAutomatic
            ? "mode-disabled"
            : !_isContrastSurfaceLoaded
                ? "surface-unloaded"
                : !_isWindowVisible
                    ? "window-hidden"
                    : _hostWindowHandle == IntPtr.Zero
                        ? "missing-window-handle"
                        : _contrastAccessibilitySettings?.HighContrast == true ||
                          WindowsCompatibilityService.IsHighContrast
                            ? "high-contrast"
                            : "active";
        LogAdaptiveContrastDiagnostic(
            $"surface={GetHashCode():X}, gate mode={WidgetForegroundSettings.ResolveMode(Config, _settingsService.Settings)} " +
            $"override={(WidgetForegroundSettings.GetModeOverride(Config) is null ? "global" : "widget")}, " +
            $"reason={gateReason}, loaded={_isContrastSurfaceLoaded}, visible={_isWindowVisible}, " +
            $"handle={_hostWindowHandle != IntPtr.Zero}, highContrast=" +
            $"{(_contrastAccessibilitySettings?.HighContrast == true || WindowsCompatibilityService.IsHighContrast)}, " +
            $"realized={_itemSurfaces.Count}, {WallpaperContrastService.GetDiagnosticStatus()}",
            $"gate:{isAutomatic}:{_isContrastSurfaceLoaded}:{_isWindowVisible}:" +
            $"{_hostWindowHandle != IntPtr.Zero}:{gateReason}",
            isGate: true);
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
        SampleAdaptiveFilenameContrast();
    }

    private void SampleAdaptiveFilenameContrast()
    {
        if (_isDisposed ||
            !_isContrastSurfaceLoaded ||
            !_isWindowVisible ||
            _hostWindowHandle == IntPtr.Zero ||
            WindowsCompatibilityService.IsHighContrast)
        {
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason=surface-state loaded={_isContrastSurfaceLoaded}, " +
                $"visible={_isWindowVisible}, handle={_hostWindowHandle != IntPtr.Zero}, " +
                $"highContrast={WindowsCompatibilityService.IsHighContrast}, realized={_itemSurfaces.Count}",
                $"pass-gate:{_isDisposed}:{_isContrastSurfaceLoaded}:{_isWindowVisible}:" +
                $"{_hostWindowHandle != IntPtr.Zero}:{WindowsCompatibilityService.IsHighContrast}");
            ResetAdaptiveFilenameLabels();
            return;
        }

        AppSettings settings = _settingsService.Settings;
        if (WidgetForegroundSettings.ResolveMode(Config, settings) !=
            WidgetForegroundSettings.ModeAutoBackground)
        {
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason=mode-disabled mode={WidgetForegroundSettings.ResolveMode(Config, settings)}, " +
                $"realized={_itemSurfaces.Count}",
                $"pass-mode:{WidgetForegroundSettings.ResolveMode(Config, settings)}");
            ResetAdaptiveFilenameLabels();
            return;
        }

        int realized = _itemSurfaces.Count;
        int eligible = 0;
        int whiteApplied = 0;
        int darkApplied = 0;
        int cacheHits = 0;
        int skippedSurface = 0;
        int skippedBounds = 0;
        int skippedUnavailable = 0;
        string? sampleExample = null;
        bool hasSolidWidgetBackground = false;
        double luminanceOverlayOpacity = 0;
        Color luminanceOverlayColor = default;
        bool expandedNameVisible = false;
        if (WidgetBackgroundCustomization.GetImageFileNameOverride(Config) is not null)
        {
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason=widget-image-override, realized={realized}",
                "pass:widget-image-override");
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
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason=global-image-background, realized={realized}",
                "pass:global-image-background");
            ResetAdaptiveFilenameLabels();
            return;
        }
        else if (WindowsCompatibilityService.ResolveWidgetMaterialType(
                     settings.WidgetShell.WidgetMaterialType) == SettingsService.WidgetMaterialTypeSolid)
        {
            var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor;
            luminanceOverlayColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                Root.ActualTheme == ElementTheme.Dark,
                accent,
                settings.WidgetShell.WidgetOpacity);
            luminanceOverlayOpacity = luminanceOverlayColor.A / 255d;
            hasSolidWidgetBackground = true;
        }
        else
        {
            // Acrylic and Mica do not expose a stable, known color overlay.
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason=unsupported-material material={settings.WidgetShell.WidgetMaterialType}, " +
                $"realized={realized}",
                $"pass:unsupported-material:{settings.WidgetShell.WidgetMaterialType}");
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
                skippedSurface++;
                if (surface is not null) ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            TextBlock name = surface.ItemNameText;
            expandedNameVisible = NameExpansionOverlay.Visibility == Visibility.Visible &&
                ReferenceEquals(_nameExpansionNameText, name);
            TextBlock sampledName = expandedNameVisible ? NameExpansionText : name;
            FrameworkElement samplingViewport = expandedNameVisible &&
                NameExpansionOverlay.Parent is FrameworkElement expansionHost
                    ? expansionHost
                    : FileItemsViewport;
            if (sampledName.Visibility != Visibility.Visible ||
                !TryGetVisiblePhysicalBounds(sampledName, samplingViewport, out WallpaperContrastRect screenBounds))
            {
                skippedBounds++;
                ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            eligible++;

            Color selectionOverlayColor = expandedNameVisible
                ? FileItemSurfaceStyleCache.GetNeutralStateLayer(
                    Root.ActualTheme == ElementTheme.Dark,
                    FileItemSurfaceVisualState.Normal,
                    isSelected: true)
                : default;
            double selectionOverlayOpacity = selectionOverlayColor.A / 255d;

            double wallpaperLuminance = 0;
            bool wallpaperSampleNeeded = !hasSolidWidgetBackground || luminanceOverlayOpacity < 1;
            long wallpaperVersion = wallpaperSampleNeeded
                ? WallpaperContrastService.SnapshotVersion
                : 0;
            bool cacheMatches = _adaptiveNameChoices.TryGetValue(
                surface,
                out AdaptiveNameSampleState cached) &&
                cached.Bounds == screenBounds &&
                ReferenceEquals(cached.Label, sampledName) &&
                cached.WallpaperVersion == wallpaperVersion &&
                cached.HasSolidWidgetBackground == hasSolidWidgetBackground &&
                cached.OverlayColor.Equals(luminanceOverlayColor) &&
                cached.OverlayOpacity.Equals(luminanceOverlayOpacity) &&
                cached.SelectionOverlayColor.Equals(selectionOverlayColor) &&
                cached.SelectionOverlayOpacity.Equals(selectionOverlayOpacity);
            bool cachedForegroundStillApplied = cacheMatches &&
                surface.HasAdaptiveNameForeground(
                    cached.PreferWhite,
                    expandedNameVisible ? sampledName : null);
            if (WallpaperContrastPolicy.CanReuseSample(
                    sampleInputsMatch: cacheMatches,
                    foregroundStillApplied: cachedForegroundStillApplied))
            {
                cacheHits++;
                if (cached.PreferWhite) whiteApplied++;
                else darkApplied++;
                continue;
            }

            bool hasWallpaperSample = wallpaperSampleNeeded &&
                WallpaperContrastService.TrySampleLuminance(screenBounds, out wallpaperLuminance);
            if (wallpaperSampleNeeded && !hasWallpaperSample)
            {
                skippedUnavailable++;
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

            if (selectionOverlayOpacity > 0)
            {
                backgroundLuminance = WallpaperContrastPolicy.BlendLuminance(
                    backgroundLuminance,
                    selectionOverlayColor.R,
                    selectionOverlayColor.G,
                    selectionOverlayColor.B,
                    selectionOverlayOpacity);
            }

            bool? previousWhite = _adaptiveNameChoices.TryGetValue(
                surface,
                out AdaptiveNameSampleState previous)
                    ? previous.PreferWhite
                    : null;
            bool preferWhite = WallpaperContrastPolicy.PreferWhite(
                backgroundLuminance,
                previousWhite);
            surface.SetAdaptiveNameForeground(
                preferWhite,
                expandedNameVisible ? sampledName : null);
            _adaptiveNameChoices[surface] = new AdaptiveNameSampleState(
                screenBounds,
                sampledName,
                wallpaperVersion,
                hasSolidWidgetBackground,
                luminanceOverlayColor,
                luminanceOverlayOpacity,
                selectionOverlayColor,
                selectionOverlayOpacity,
                preferWhite);
            if (preferWhite) whiteApplied++;
            else darkApplied++;
            sampleExample ??= $"bounds=({screenBounds.Left:F0},{screenBounds.Top:F0})-" +
                $"({screenBounds.Right:F0},{screenBounds.Bottom:F0}), " +
                $"luminance={backgroundLuminance:F3}, foreground={(preferWhite ? "white" : "dark")}";
        }

        LogAdaptiveContrastDiagnostic(
            $"pass realized={realized}, eligible={eligible}, white={whiteApplied}, dark={darkApplied}, " +
            $"cacheHits={cacheHits}, skippedSurface={skippedSurface}, skippedBounds={skippedBounds}, " +
            $"skippedUnavailable={skippedUnavailable}, snapshot={WallpaperContrastService.SnapshotVersion}, " +
            $"example={sampleExample ?? "none"}, {WallpaperContrastService.GetDiagnosticStatus()}",
            $"pass:{realized}:{eligible}:{whiteApplied}:{darkApplied}:{skippedSurface}:" +
            $"{skippedBounds}:{skippedUnavailable}:{WallpaperContrastService.SnapshotVersion}");
    }

    private void LogAdaptiveContrastDiagnostic(
        string details,
        string signature,
        bool isGate = false)
    {
        DateTime now = DateTime.UtcNow;
        string? previousSignature = isGate
            ? _lastContrastGateSignature
            : _lastContrastPassSignature;
        DateTime previousUtc = isGate
            ? _lastContrastGateDiagnosticUtc
            : _lastContrastPassDiagnosticUtc;
        if (!isGate && previousUtc != DateTime.MinValue &&
            now - previousUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        if (string.Equals(signature, previousSignature, StringComparison.Ordinal) &&
            now - previousUtc < TimeSpan.FromSeconds(60))
        {
            return;
        }

        if (isGate)
        {
            _lastContrastGateSignature = signature;
            _lastContrastGateDiagnosticUtc = now;
        }
        else
        {
            _lastContrastPassSignature = signature;
            _lastContrastPassDiagnosticUtc = now;
        }

        App.Log($"[WallpaperContrast] surface={GetHashCode():X}, {details}");
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
            if (!Win32Helper.ClientToScreen(_hostWindowHandle, ref clientOrigin))
            {
                LogAdaptiveContrastDiagnostic(
                    "visible-bounds unavailable reason=ClientToScreen-failed",
                    "bounds-client-to-screen-failed");
                return false;
            }
            double scale = Win32Helper.GetDpiScaleForWindow(
                _hostWindowHandle,
                viewport.XamlRoot);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                LogAdaptiveContrastDiagnostic(
                    $"visible-bounds unavailable reason=invalid-dpi-scale scale={scale}",
                    $"bounds-invalid-dpi:{scale}");
                return false;
            }

            screenBounds = new WallpaperContrastRect(
                clientOrigin.X + (visible.X * scale),
                clientOrigin.Y + (visible.Y * scale),
                clientOrigin.X + (visible.X + visible.Width) * scale,
                clientOrigin.Y + (visible.Y + visible.Height) * scale);
            return screenBounds.Width > 0 && screenBounds.Height > 0;
        }
        catch (Exception ex)
        {
            LogAdaptiveContrastDiagnostic(
                $"visible-bounds transform failed exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}",
                $"bounds-transform:{ex.GetType().Name}:{ex.HResult}");
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
        catch (Exception ex)
        {
            _contrastAccessibilitySettings = null;
            LogAdaptiveContrastDiagnostic(
                $"accessibility watcher unavailable exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}",
                $"accessibility-watcher:{ex.GetType().Name}:{ex.HResult}");
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
        foreach ((FileItemSurface surface, AdaptiveNameSampleState state) in _adaptiveNameChoices.ToArray())
        {
            surface.SetAdaptiveNameForeground(
                null,
                ReferenceEquals(state.Label, NameExpansionText)
                    ? NameExpansionText
                    : null);
        }
        _adaptiveNameChoices.Clear();
        NameExpansionText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void ClearAdaptiveFilenameChoice(FileItemSurface surface)
    {
        if (_adaptiveNameChoices.Remove(surface, out AdaptiveNameSampleState state))
        {
            bool clearExpandedLabel = ReferenceEquals(state.Label, NameExpansionText) &&
                ReferenceEquals(_nameExpansionNameText, surface.ItemNameText);
            surface.SetAdaptiveNameForeground(
                null,
                clearExpandedLabel ? NameExpansionText : null);
        }
    }

    private readonly record struct AdaptiveNameSampleState(
        WallpaperContrastRect Bounds,
        TextBlock Label,
        long WallpaperVersion,
        bool HasSolidWidgetBackground,
        Color OverlayColor,
        double OverlayOpacity,
        Color SelectionOverlayColor,
        double SelectionOverlayOpacity,
        bool PreferWhite);
}
