using DeskBox.Helpers;
using DeskBox.Contracts;
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
    private readonly WallpaperContrastGeometryCache<FileItemSurface, AdaptiveNameGeometry> _adaptiveNameGeometry =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WidgetItem, int> _adaptiveItemIndexes =
        new(ReferenceEqualityComparer.Instance);
    private IDisposable? _wallpaperContrastLease;
    private IDisposable? _wallpaperSnapshotSubscription;
    private AccessibilitySettings? _contrastAccessibilitySettings;
    private bool _isContrastSurfaceLoaded;
    private bool _adaptiveContrastSampleQueued;
    private bool _adaptiveNameGeometryInvalidationPending;
    private DispatcherQueueTimer? _adaptiveRepositionRefreshTimer;
    private DateTime _adaptiveRepositionRefreshUntilUtc;
    private bool _adaptiveItemIndexesDirty = true;
    private bool _adaptiveGridOriginValid;
    private Windows.Foundation.Point _adaptiveGridOriginInViewport;
    private bool _adaptiveViewportOriginValid;
    private Windows.Foundation.Point _adaptiveViewportOriginInRoot;
    private bool _adaptiveScreenTransformValid;
    private Win32Helper.POINT _adaptiveClientOrigin;
    private double _adaptiveDpiScale;
    private TextBlock? _expandedAdaptiveLabel;
    private WallpaperContrastRect? _expandedAdaptiveBounds;
    private string? _lastContrastGateSignature;
    private DateTime _lastContrastGateDiagnosticUtc = DateTime.MinValue;
    private string? _lastContrastPassSignature;
    private DateTime _lastContrastPassDiagnosticUtc = DateTime.MinValue;

    internal void RefreshAdaptiveFilenameContrast() =>
        UpdateAdaptiveFilenameContrast(invalidateGeometry: false);

    private void FileSurfaceContent_SettingsChanged() =>
        UpdateAdaptiveFilenameContrast(invalidateGeometry: false);

    private void FileItemsViewport_SizeChangedForContrast(
        object sender,
        SizeChangedEventArgs e) =>
        QueueAdaptiveFilenameContrastSample(invalidateGeometry: true);

    private void ExpandedNameText_SizeChangedForContrast(
        object sender,
        SizeChangedEventArgs e)
    {
        _expandedAdaptiveLabel = null;
        _expandedAdaptiveBounds = null;
        QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
    }

    private void RenderedItems_CollectionChangedForContrast(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnAdaptiveFilenameContrastItemsChanged(e);

    private void ItemSurface_SizeChangedForContrast(
        object sender,
        SizeChangedEventArgs e)
    {
        if (sender is FileItemSurface surface)
        {
            InvalidateAdaptiveNameGeometry(surface);
            QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
        }
    }

    private void ItemNameText_SizeChangedForContrast(
        object sender,
        SizeChangedEventArgs e)
    {
        if (sender is TextBlock text && FileItemSurface.FindOwner(text) is { } surface)
        {
            InvalidateAdaptiveNameGeometry(surface);
            QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
        }
    }

    private void UpdateAdaptiveFilenameContrast(bool invalidateGeometry = true)
    {
        if (_isDisposed) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => UpdateAdaptiveFilenameContrast(invalidateGeometry));
            return;
        }

        if (invalidateGeometry)
        {
            InvalidateAdaptiveNameGeometry();
            _adaptiveScreenTransformValid = false;
        }

        WidgetShellSettingsSlice captureSettings = _settingsService.Settings.WidgetShell;
        WallpaperContrastSource source =
            WallpaperCapturePreferenceKinds.NormalizeSource(captureSettings.WallpaperCaptureSource) switch
            {
                WallpaperCapturePreferenceKinds.SourceDesktopHost => WallpaperContrastSource.DesktopHost,
                WallpaperCapturePreferenceKinds.SourceSystemWallpaper => WallpaperContrastSource.SystemWallpaper,
                _ => WallpaperContrastSource.Auto
            };
        WallpaperHostCaptureMethod method =
            WallpaperCapturePreferenceKinds.NormalizeMethod(captureSettings.WallpaperHostCaptureMethod) switch
            {
                WallpaperCapturePreferenceKinds.MethodFullWindow => WallpaperHostCaptureMethod.FullWindow,
                WallpaperCapturePreferenceKinds.MethodLegacyWindow => WallpaperHostCaptureMethod.LegacyWindow,
                _ => WallpaperHostCaptureMethod.Auto
            };
        int refreshIntervalSeconds =
            WallpaperCapturePreferenceKinds.NormalizeRefreshIntervalSeconds(
                captureSettings.WallpaperCaptureRefreshIntervalSeconds);
        WallpaperContrastService.ConfigureCaptureOptions(new WallpaperCaptureOptions(
            source,
            method,
            refreshIntervalSeconds));

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

        _wallpaperSnapshotSubscription ??=
            WallpaperContrastService.SubscribeToSnapshotChanges(
                DispatcherQueue,
                _ => QueueAdaptiveFilenameContrastSample(invalidateGeometry: false));
        _wallpaperContrastLease ??= WallpaperContrastService.Acquire();

        SampleAdaptiveFilenameContrast();
    }

    internal void RefreshAdaptiveFilenameContrastForWindowGeometry(bool localGeometryChanged)
    {
        if (_isDisposed)
        {
            return;
        }

        _adaptiveScreenTransformValid = false;
        if (localGeometryChanged)
        {
            InvalidateAdaptiveNameGeometry();
        }

        QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
    }

    internal void OnAdaptiveFilenameContrastViewportSizeChanged() =>
        QueueAdaptiveFilenameContrastSample(invalidateGeometry: true);

    internal void OnAdaptiveFilenameContrastItemsChanged(
        System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.Action is not System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
        {
            StartAdaptiveRepositionRefresh();
        }

        _adaptiveItemIndexesDirty = true;
        foreach (FileItemSurface surface in _adaptiveNameGeometry.Entries
                     .Where(pair => !pair.Value.IsIcon)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _adaptiveNameGeometry.Remove(surface);
        }

        QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
    }

    private void StartAdaptiveRepositionRefresh()
    {
        if (_isDisposed || _wallpaperContrastLease is null ||
            !_isContrastSurfaceLoaded || !_isWindowVisible)
        {
            return;
        }

        _adaptiveRepositionRefreshUntilUtc = DateTime.UtcNow.AddMilliseconds(450);
        if (_adaptiveRepositionRefreshTimer is null)
        {
            _adaptiveRepositionRefreshTimer = DispatcherQueue.CreateTimer();
            _adaptiveRepositionRefreshTimer.Interval = TimeSpan.FromMilliseconds(33);
            _adaptiveRepositionRefreshTimer.IsRepeating = true;
            _adaptiveRepositionRefreshTimer.Tick += AdaptiveRepositionRefreshTimer_Tick;
            _adaptiveRepositionRefreshTimer.Start();
        }
    }

    private void AdaptiveRepositionRefreshTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed || _wallpaperContrastLease is null ||
            DateTime.UtcNow >= _adaptiveRepositionRefreshUntilUtc)
        {
            StopAdaptiveRepositionRefresh();
            QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
            return;
        }

        QueueAdaptiveFilenameContrastSample(invalidateGeometry: false);
    }

    private void StopAdaptiveRepositionRefresh()
    {
        if (_adaptiveRepositionRefreshTimer is not { } timer)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= AdaptiveRepositionRefreshTimer_Tick;
        _adaptiveRepositionRefreshTimer = null;
    }

    private void QueueAdaptiveFilenameContrastSample(bool invalidateGeometry)
    {
        if (_isDisposed)
        {
            return;
        }

        _adaptiveNameGeometryInvalidationPending |= invalidateGeometry;
        if (_adaptiveContrastSampleQueued)
        {
            return;
        }

        _adaptiveContrastSampleQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _adaptiveContrastSampleQueued = false;
                if (_isDisposed)
                {
                    return;
                }

                if (_adaptiveNameGeometryInvalidationPending)
                {
                    _adaptiveNameGeometryInvalidationPending = false;
                    InvalidateAdaptiveNameGeometry();
                }

                SampleAdaptiveFilenameContrast();
            }))
        {
            _adaptiveContrastSampleQueued = false;
        }
    }

    private void InvalidateAdaptiveNameGeometry(FileItemSurface? surface = null)
    {
        if (surface is null)
        {
            _adaptiveNameGeometry.Clear();
            _expandedAdaptiveLabel = null;
            _expandedAdaptiveBounds = null;
            _adaptiveGridOriginValid = false;
            _adaptiveViewportOriginValid = false;
            return;
        }

        if (surface.Mode == FileItemSurfaceMode.List)
        {
            foreach (FileItemSurface listSurface in _adaptiveNameGeometry.Entries
                         .Where(pair => !pair.Value.IsIcon)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _adaptiveNameGeometry.Remove(listSurface);
            }
        }
        else
        {
            _adaptiveNameGeometry.Remove(surface);
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
        double thresholdPercent = WidgetForegroundContrastThresholdKinds.NormalizePercent(
            settings.WidgetShell.WidgetForegroundContrastThresholdPercent);
        double threshold = WidgetForegroundContrastThresholdKinds.ToLinearLuminance(
            thresholdPercent);
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
        Color effectiveAccentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        bool hasSolidWidgetBackground;
        double luminanceOverlayOpacity;
        Color luminanceOverlayColor;
        bool expandedNameVisible = false;
        ListViewBase activeItemsView = GetActiveItemsView();
        if (!WidgetAutoBackgroundContrastPolicy.TryResolveSurface(
                Config,
                settings,
                Root.ActualTheme == ElementTheme.Dark,
                effectiveAccentColor,
                out WidgetAutoBackgroundSurface backgroundSurface))
        {
            string reason = backgroundSurface.Support switch
            {
                WidgetAutoBackgroundSupport.WidgetImageOverride => "widget-image-override",
                WidgetAutoBackgroundSupport.GlobalImageBackground => "global-image-background",
                _ => $"unsupported-material:{settings.WidgetShell.WidgetMaterialType}"
            };
            LogAdaptiveContrastDiagnostic(
                $"pass skipped reason={reason}, realized={realized}",
                $"pass:{reason}");
            ResetAdaptiveFilenameLabels();
            return;
        }

        hasSolidWidgetBackground = backgroundSurface.HasSolidWidgetBackground;
        luminanceOverlayOpacity = backgroundSurface.OverlayOpacity;
        luminanceOverlayColor = backgroundSurface.OverlayColor;

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
            if (sampledName.Visibility != Visibility.Visible ||
                !TryGetAdaptiveNameBounds(
                    surface,
                    item,
                    sampledName,
                    expandedNameVisible,
                    out WallpaperContrastRect screenBounds))
            {
                skippedBounds++;
                ClearAdaptiveFilenameChoice(surface);
                continue;
            }

            eligible++;

            bool isSelected = expandedNameVisible || activeItemsView.SelectedItems.Contains(item);
            bool isDropTarget = ReferenceEquals(border, _folderDropTarget) ||
                ReferenceEquals(border, _launchDropTarget);
            FileItemSurfaceVisualState visualState = isDropTarget
                ? FileItemSurfaceVisualState.Hover
                : surface.VisualState;
            Color selectionOverlayColor = FileItemSurfaceStyleCache.GetNeutralStateLayer(
                Root.ActualTheme == ElementTheme.Dark,
                expandedNameVisible ? FileItemSurfaceVisualState.Normal : visualState,
                isSelected);
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
                    foregroundStillApplied: cachedForegroundStillApplied,
                    cachedThresholdPercent: cached.ThresholdPercent,
                    currentThresholdPercent: thresholdPercent))
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
                out AdaptiveNameSampleState previous) &&
                previous.ThresholdPercent.Equals(thresholdPercent) &&
                previous.SelectionOverlayColor.Equals(selectionOverlayColor) &&
                previous.SelectionOverlayOpacity.Equals(selectionOverlayOpacity)
                    ? previous.PreferWhite
                    : null;
            bool preferWhite = WallpaperContrastPolicy.PreferWhite(
                backgroundLuminance,
                previousWhite,
                threshold: threshold);
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
                thresholdPercent,
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

    private bool TryGetAdaptiveNameBounds(
        FileItemSurface surface,
        WidgetItem item,
        TextBlock sampledName,
        bool isExpanded,
        out WallpaperContrastRect screenBounds)
    {
        screenBounds = default;
        if (!isExpanded && !IsSurfaceInMainItemsView(surface))
        {
            return false;
        }

        WallpaperContrastRect? localBounds;
        if (isExpanded)
        {
            if (!ReferenceEquals(_expandedAdaptiveLabel, sampledName) ||
                _expandedAdaptiveBounds is null)
            {
                if (!TryMeasureElementBoundsInViewport(sampledName, out WallpaperContrastRect measured))
                {
                    return false;
                }

                _expandedAdaptiveLabel = sampledName;
                _expandedAdaptiveBounds = measured;
            }

            localBounds = _expandedAdaptiveBounds;
        }
        else if (_adaptiveRepositionRefreshTimer is not null)
        {
            // RepositionThemeTransition moves realized visuals between their
            // final grid/list slots. Follow those compositor transforms only
            // while this bounded animation window is active; settled items
            // return to their cached row/column geometry immediately after.
            if (!TryMeasureElementBoundsInViewport(sampledName, out WallpaperContrastRect movingBounds))
            {
                return false;
            }

            localBounds = movingBounds;
        }
        else
        {
            if (!TryGetCachedNameGeometry(surface, item, sampledName, out AdaptiveNameGeometry geometry))
            {
                return false;
            }

            ScrollViewer? scrollViewer = GetAdaptiveContrastScrollViewer(geometry.IsIcon);
            double horizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
            double verticalOffset = scrollViewer?.VerticalOffset ?? 0;
            if (geometry.IsIcon)
            {
                double viewportWidth = scrollViewer?.ViewportWidth > 0
                    ? scrollViewer.ViewportWidth
                    : ItemsGrid.ActualWidth;
                int columns = ResolveAdaptiveGridColumnCount(viewportWidth);
                if (!TryGetAdaptiveGridOrigin(out Windows.Foundation.Point gridOrigin))
                {
                    return false;
                }

                Thickness margin = ViewModel.IconTileMargin;
                localBounds = WallpaperContrastGeometry.GetGridTextBounds(
                    geometry.Index,
                    columns,
                    ViewModel.IconCellWidth,
                    ViewModel.IconCellHeight,
                    margin.Left,
                    margin.Top,
                    geometry.LocalTextBounds,
                    gridOrigin.X,
                    gridOrigin.Y,
                    horizontalOffset,
                    verticalOffset,
                    FileItemsViewport.ActualWidth,
                    FileItemsViewport.ActualHeight);
            }
            else
            {
                localBounds = WallpaperContrastGeometry.GetScrolledListTextBounds(
                    geometry.LocalTextBounds,
                    geometry.MeasuredHorizontalOffset,
                    geometry.MeasuredVerticalOffset,
                    horizontalOffset,
                    verticalOffset,
                    FileItemsViewport.ActualWidth,
                    FileItemsViewport.ActualHeight);
            }
        }

        if (localBounds is null || localBounds.Value.Width <= 0 || localBounds.Value.Height <= 0)
        {
            return false;
        }

        if (!_adaptiveScreenTransformValid)
        {
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
                FileItemsViewport.XamlRoot);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                LogAdaptiveContrastDiagnostic(
                    $"visible-bounds unavailable reason=invalid-dpi-scale scale={scale}",
                    $"bounds-invalid-dpi:{scale}");
                return false;
            }

            _adaptiveClientOrigin = clientOrigin;
            _adaptiveDpiScale = scale;
            _adaptiveScreenTransformValid = true;
        }

        if (!_adaptiveViewportOriginValid &&
            !TryGetAdaptiveViewportOriginInRoot(out _adaptiveViewportOriginInRoot))
        {
            return false;
        }

        screenBounds = WallpaperContrastGeometry.ToScreen(
            localBounds.Value,
            _adaptiveViewportOriginInRoot.X,
            _adaptiveViewportOriginInRoot.Y,
            _adaptiveClientOrigin.X,
            _adaptiveClientOrigin.Y,
            _adaptiveDpiScale);
        return screenBounds.Width > 0 && screenBounds.Height > 0;
    }

    private bool TryGetAdaptiveViewportOriginInRoot(out Windows.Foundation.Point origin)
    {
        origin = default;
        UIElement? root = FileItemsViewport.XamlRoot?.Content;
        if (root is null)
        {
            return false;
        }

        try
        {
            origin = FileItemsViewport.TransformToVisual(root).TransformPoint(default);
            _adaptiveViewportOriginInRoot = origin;
            _adaptiveViewportOriginValid = true;
            return true;
        }
        catch (Exception ex)
        {
            LogAdaptiveContrastDiagnostic(
                $"viewport-root-offset transform failed exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}",
                $"viewport-root-offset-transform:{ex.GetType().Name}:{ex.HResult}");
            return false;
        }
    }

    private bool TryGetCachedNameGeometry(
        FileItemSurface surface,
        WidgetItem item,
        TextBlock label,
        out AdaptiveNameGeometry geometry)
    {
        bool isIcon = surface.Mode == FileItemSurfaceMode.Icon;
        if (!_adaptiveNameGeometry.TryGetOrMeasure(
                surface,
                cached => ReferenceEquals(cached.Item, item) &&
                    ReferenceEquals(cached.Label, label) &&
                    cached.IsIcon == isIcon,
                () =>
                {
                    bool measured = TryMeasureAdaptiveNameGeometry(
                        surface,
                        item,
                        label,
                        isIcon,
                        out AdaptiveNameGeometry result);
                    return (measured, result);
                },
                out geometry))
        {
            return false;
        }

        if (isIcon)
        {
            EnsureAdaptiveItemIndexes();
            if (!_adaptiveItemIndexes.TryGetValue(item, out int currentIndex))
            {
                _adaptiveNameGeometry.Remove(surface);
                geometry = default;
                return false;
            }

            if (geometry.Index != currentIndex)
            {
                geometry = geometry with { Index = currentIndex };
                _adaptiveNameGeometry.Set(surface, geometry);
            }
        }

        return true;
    }

    private bool TryMeasureAdaptiveNameGeometry(
        FileItemSurface surface,
        WidgetItem item,
        TextBlock label,
        bool isIcon,
        out AdaptiveNameGeometry geometry)
    {
        if (label.ActualWidth <= 0 || label.ActualHeight <= 0)
        {
            geometry = default;
            return false;
        }

        WallpaperContrastRect localBounds;
        double measuredHorizontalOffset = 0;
        double measuredVerticalOffset = 0;
        if (isIcon)
        {
            Windows.Foundation.Rect bounds;
            try
            {
                bounds = label.TransformToVisual(surface)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, label.ActualWidth, label.ActualHeight));
            }
            catch (Exception ex)
            {
                LogAdaptiveContrastDiagnostic(
                    $"item-local-bounds transform failed exception={ex.GetType().Name}/" +
                    $"0x{unchecked((uint)ex.HResult):X8}",
                    $"item-local-bounds-transform:{ex.GetType().Name}:{ex.HResult}");
                geometry = default;
                return false;
            }
            localBounds = new WallpaperContrastRect(
                bounds.X,
                bounds.Y,
                bounds.X + bounds.Width,
                bounds.Y + bounds.Height);
            EnsureAdaptiveItemIndexes();
            if (!_adaptiveItemIndexes.TryGetValue(item, out int index))
            {
                geometry = default;
                return false;
            }

            geometry = new AdaptiveNameGeometry(
                item,
                label,
                isIcon,
                index,
                localBounds,
                measuredHorizontalOffset,
                measuredVerticalOffset);
        }
        else
        {
            if (!TryMeasureElementBoundsInViewport(label, out localBounds))
            {
                geometry = default;
                return false;
            }

            ScrollViewer? scrollViewer = GetAdaptiveContrastScrollViewer(isIcon: false);
            measuredHorizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
            measuredVerticalOffset = scrollViewer?.VerticalOffset ?? 0;
            geometry = new AdaptiveNameGeometry(
                item,
                label,
                isIcon,
                -1,
                localBounds,
                measuredHorizontalOffset,
                measuredVerticalOffset);
        }

        return true;
    }

    private bool TryMeasureElementBoundsInViewport(
        FrameworkElement element,
        out WallpaperContrastRect bounds)
    {
        bounds = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            FileItemsViewport.ActualWidth <= 0 || FileItemsViewport.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Rect transformed = element.TransformToVisual(FileItemsViewport)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bounds = new WallpaperContrastRect(
                transformed.X,
                transformed.Y,
                transformed.X + transformed.Width,
                transformed.Y + transformed.Height);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch (Exception ex)
        {
            LogAdaptiveContrastDiagnostic(
                $"local-bounds transform failed exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}",
                $"local-bounds-transform:{ex.GetType().Name}:{ex.HResult}");
            return false;
        }
    }

    private ScrollViewer? GetAdaptiveContrastScrollViewer(bool isIcon) =>
        isIcon
            ? GetRenderWindowScrollViewer(ItemsGrid)
            : GetRenderWindowScrollViewer(ItemsList);

    private int ResolveAdaptiveGridColumnCount(double viewportWidth)
    {
        double width = ItemsGrid.ItemsPanelRoot is ItemsWrapGrid panel && panel.ActualWidth > 0
            ? Math.Min(panel.ActualWidth, viewportWidth > 0 ? viewportWidth : panel.ActualWidth)
            : viewportWidth;
        return ViewModel.IconCellWidth > 0 && width > 0
            ? Math.Max(1, (int)Math.Floor((width + 0.01) / ViewModel.IconCellWidth))
            : 1;
    }

    private bool TryGetAdaptiveGridOrigin(out Windows.Foundation.Point origin)
    {
        origin = default;
        if (_adaptiveGridOriginValid)
        {
            origin = _adaptiveGridOriginInViewport;
            return true;
        }

        try
        {
            _adaptiveGridOriginInViewport = ItemsGrid.TransformToVisual(FileItemsViewport)
                .TransformPoint(default);
            _adaptiveGridOriginValid = true;
            origin = _adaptiveGridOriginInViewport;
            return true;
        }
        catch (Exception ex)
        {
            LogAdaptiveContrastDiagnostic(
                $"grid-origin transform failed exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}",
                $"grid-origin-transform:{ex.GetType().Name}:{ex.HResult}");
            return false;
        }
    }

    private void EnsureAdaptiveItemIndexes()
    {
        if (!_adaptiveItemIndexesDirty)
        {
            return;
        }

        _adaptiveItemIndexes.Clear();
        for (int index = 0; index < ViewModel.RenderedItems.Count; index++)
        {
            _adaptiveItemIndexes[ViewModel.RenderedItems[index]] = index;
        }

        _adaptiveItemIndexesDirty = false;
    }

    private bool IsSurfaceInMainItemsView(FileItemSurface surface)
    {
        DependencyObject? current = surface;
        while (current is not null)
        {
            if (ReferenceEquals(current, ItemsGrid) || ReferenceEquals(current, ItemsList))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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
        if (!DispatcherQueue.TryEnqueue(() => UpdateAdaptiveFilenameContrast()))
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
        StopAdaptiveRepositionRefresh();
        _wallpaperSnapshotSubscription?.Dispose();
        _wallpaperSnapshotSubscription = null;
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
        double ThresholdPercent,
        bool PreferWhite);

    private readonly record struct AdaptiveNameGeometry(
        WidgetItem Item,
        TextBlock Label,
        bool IsIcon,
        int Index,
        WallpaperContrastRect LocalTextBounds,
        double MeasuredHorizontalOffset,
        double MeasuredVerticalOffset);
}
