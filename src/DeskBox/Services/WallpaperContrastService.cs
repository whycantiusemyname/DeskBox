using System.Runtime.InteropServices;
using DeskBox.Platform;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace DeskBox.Services;

/// <summary>
/// Shared, downsampled cache of the Windows desktop wallpaper. All COM, file
/// metadata and image decoder work runs on a background worker; callers only
/// read the immutable maps while sampling realized file-name labels.
/// </summary>
internal static class WallpaperContrastService
{
    private static readonly TimeSpan s_captureWorkerTimeout = TimeSpan.FromSeconds(12);
    private static readonly object s_gate = new();
    private static readonly object s_optionsGate = new();
    private static readonly object s_captureWorkerGate = new();
    private static readonly Dictionary<string, CachedBitmap> s_bitmapCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static WallpaperSnapshot? s_snapshot;
    private static long s_snapshotVersion;
    private static CancellationTokenSource? s_refreshCancellation;
    private static Task? s_refreshWorker;
    private static int s_clients;
    private static string? s_lastRefreshError;
    private static WallpaperCaptureOptions s_captureOptions = new();
    private static long s_captureOptionsVersion;
    private static TaskCompletionSource s_captureOptionsChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CaptureWorker? s_captureWorker;

    public static IDisposable Acquire()
    {
        int clients;
        bool started = false;
        lock (s_gate)
        {
            s_clients++;
            clients = s_clients;
            if (s_clients == 1)
            {
                s_refreshCancellation = new CancellationTokenSource();
                CancellationToken token = s_refreshCancellation.Token;
                s_refreshWorker = Task.Run(() => RefreshLoopAsync(token));
                started = true;
            }
        }

        App.Log($"[WallpaperContrast] client acquired clients={clients}, workerStarted={started}");

        return new ClientLease();
    }

    public static void ConfigureCaptureOptions(WallpaperCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WallpaperCaptureOptions normalized = options.Normalize();
        TaskCompletionSource changed;
        lock (s_optionsGate)
        {
            WallpaperCaptureOptions current = Volatile.Read(ref s_captureOptions);
            if (current == normalized)
            {
                return;
            }

            Volatile.Write(ref s_captureOptions, normalized);
            Interlocked.Increment(ref s_captureOptionsVersion);
            changed = s_captureOptionsChanged;
            s_captureOptionsChanged = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        App.Log($"[WallpaperContrast] capture options source={normalized.Source}, " +
            $"method={normalized.Method}, intervalSeconds={normalized.RefreshIntervalSeconds}");
    }

    public static string GetDiagnosticStatus()
    {
        lock (s_gate)
        {
            long version = Volatile.Read(ref s_snapshot)?.Version ?? 0;
            string worker = s_refreshWorker is { IsCompleted: false } ? "running" : "stopped";
            string error = Volatile.Read(ref s_lastRefreshError) ?? "none";
            return $"serviceClients={s_clients}, worker={worker}, snapshot={version}, lastError={error}";
        }
    }

    public static bool TrySampleLuminance(
        WallpaperContrastRect screenBounds,
        out double luminance)
    {
        WallpaperSnapshot? snapshot = Volatile.Read(ref s_snapshot);
        if (snapshot is null)
        {
            luminance = 0;
            return false;
        }

        return snapshot.TrySampleLuminance(screenBounds, out luminance);
    }

    public static long SnapshotVersion =>
        Volatile.Read(ref s_snapshot)?.Version ?? 0;

    private static async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            long observedOptionsVersion = Volatile.Read(ref s_captureOptionsVersion);
            await TryRefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long currentOptionsVersion = Volatile.Read(ref s_captureOptionsVersion);
                if (currentOptionsVersion != observedOptionsVersion)
                {
                    observedOptionsVersion = currentOptionsVersion;
                    await TryRefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                WallpaperCaptureOptions options = Volatile.Read(ref s_captureOptions);
                Task delay = Task.Delay(
                    TimeSpan.FromSeconds(options.RefreshIntervalSeconds),
                    cancellationToken);
                Task optionsChanged = GetOptionsChangedTask();
                if (Volatile.Read(ref s_captureOptionsVersion) != observedOptionsVersion)
                {
                    continue;
                }

                await Task.WhenAny(delay, optionsChanged).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                currentOptionsVersion = Volatile.Read(ref s_captureOptionsVersion);
                if (currentOptionsVersion != observedOptionsVersion)
                {
                    observedOptionsVersion = currentOptionsVersion;
                }

                await TryRefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The last visible AutoBackground file surface releases the cache.
        }
        finally
        {
            lock (s_gate)
            {
                if (s_refreshCancellation?.Token == cancellationToken)
                {
                    s_refreshCancellation.Dispose();
                    s_refreshCancellation = null;
                    s_refreshWorker = null;
                }
            }
        }
    }

    private static Task GetOptionsChangedTask()
    {
        lock (s_optionsGate)
        {
            return s_captureOptionsChanged.Task;
        }
    }

    private static async Task<WallpaperHostCaptureResult> CaptureHostFramesAsync(
        IReadOnlyList<WallpaperContrastRect> monitorBounds,
        WallpaperCaptureOptions options,
        CancellationToken cancellationToken)
    {
        CaptureWorker worker;
        bool consumeCompletedWorker;
        lock (s_captureWorkerGate)
        {
            if (s_captureWorker is { Task.IsCompleted: false })
            {
                return new WallpaperHostCaptureResult(
                    "capture-busy",
                    new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>(),
                    0,
                    0,
                    0,
                    0,
                    string.Empty);
            }

            if (s_captureWorker is { } completed &&
                completed.Source == options.Source &&
                completed.Method == options.Method &&
                completed.MonitorBounds.SequenceEqual(monitorBounds))
            {
                worker = completed;
                consumeCompletedWorker = true;
                s_captureWorker = null;
            }
            else
            {
                WallpaperContrastRect[] bounds = monitorBounds.ToArray();
                worker = new CaptureWorker(
                    options.Source,
                    options.Method,
                    bounds,
                    Task.Run(() => WallpaperHostCaptureBackend.Capture(bounds, options.Method)));
                s_captureWorker = worker;
                consumeCompletedWorker = false;
            }
        }

        try
        {
            if (!consumeCompletedWorker)
            {
                await worker.Task.WaitAsync(s_captureWorkerTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            WallpaperHostCaptureResult result = await worker.Task.ConfigureAwait(false);
            lock (s_captureWorkerGate)
            {
                if (ReferenceEquals(s_captureWorker, worker))
                {
                    s_captureWorker = null;
                }
            }

            return result;
        }
        catch (TimeoutException)
        {
            return new WallpaperHostCaptureResult(
                "capture-timeout",
                new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>(),
                0,
                0,
                0,
                (long)s_captureWorkerTimeout.TotalMilliseconds,
                string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (worker.Task.IsCompleted)
            {
                lock (s_captureWorkerGate)
                {
                    if (ReferenceEquals(s_captureWorker, worker))
                    {
                        s_captureWorker = null;
                    }
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[WallpaperContrast] host capture worker failed " +
                $"exception={ex.GetType().Name}/0x{unchecked((uint)ex.HResult):X8}");
            lock (s_captureWorkerGate)
            {
                if (ReferenceEquals(s_captureWorker, worker))
                {
                    s_captureWorker = null;
                }
            }

            return new WallpaperHostCaptureResult(
                "capture-failed",
                new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>(),
                0,
                0,
                0,
                0,
                string.Empty);
        }
    }

    private static async Task TryRefreshSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transient COM, file, or decoder failure preserves the last
            // usable map and leaves the independent 60-second retry alive.
            string error = $"{ex.GetType().Name}/0x{unchecked((uint)ex.HResult):X8}";
            Volatile.Write(ref s_lastRefreshError, error);
            App.Log($"[WallpaperContrast] refresh failed error={error}, " +
                $"snapshot={SnapshotVersion}; retry={Volatile.Read(ref s_captureOptions).RefreshIntervalSeconds}s");
        }
    }

    private static async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        WallpaperSnapshotData? data = await Task.Run(
            ReadDesktopWallpaper,
            cancellationToken).ConfigureAwait(false);
        if (data is null || cancellationToken.IsCancellationRequested)
        {
            if (data is null)
            {
                Volatile.Write(ref s_lastRefreshError, "desktop-wallpaper-source-unavailable");
                App.Log($"[WallpaperContrast] refresh produced no snapshot; " +
                    $"snapshot={SnapshotVersion}; retry={Volatile.Read(ref s_captureOptions).RefreshIntervalSeconds}s");
            }
            return;
        }

        WallpaperCaptureOptions captureOptions = Volatile.Read(ref s_captureOptions);
        WallpaperSnapshot? previousSnapshot = Volatile.Read(ref s_snapshot);
        WallpaperHostCaptureResult hostCapture = captureOptions.Source == WallpaperContrastSource.SystemWallpaper
            ? WallpaperHostCaptureResult.Disabled
            : await CaptureHostFramesAsync(
                data.Monitors.Select(monitor => monitor.Bounds).ToArray(),
                captureOptions,
                cancellationToken).ConfigureAwait(false);
        var monitors = new List<WallpaperMonitor>(data.Monitors.Count);
        foreach (WallpaperMonitorData monitor in data.Monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WallpaperDynamicBitmap? dynamicBitmap = null;
            bool hasCurrentHostCapture = hostCapture.Maps.TryGetValue(
                monitor.Bounds,
                out WallpaperDynamicBitmap? capturedBitmap);
            if (hasCurrentHostCapture)
            {
                dynamicBitmap = capturedBitmap;
            }
            else if (hostCapture.Status is not "no-host" and not "disabled" &&
                     previousSnapshot?.FindReusableDynamicMonitor(
                         monitor.Bounds,
                         captureOptions.Source,
                         captureOptions.Method) is { } previousDynamic)
            {
                // Preserve the last good host frame across a transient capture
                // failure, timeout or still-running PrintWindow worker.
                dynamicBitmap = previousDynamic;
            }

            WallpaperBitmap? bitmap = null;
            bool imagePathWasPresent = !string.IsNullOrWhiteSpace(monitor.ImagePath);
            string? imageIdentity = imagePathWasPresent
                ? GetBitmapCacheKey(monitor.ImagePath!)
                : null;
            bool sourceUsesStaticFallback =
                captureOptions.Source == WallpaperContrastSource.SystemWallpaper ||
                (captureOptions.Source == WallpaperContrastSource.Auto && dynamicBitmap is null);
            if (imagePathWasPresent && sourceUsesStaticFallback)
            {
                bitmap = await GetOrDecodeBitmapAsync(monitor.ImagePath!, cancellationToken)
                    .ConfigureAwait(false);
                WallpaperMonitor? previousMonitor = previousSnapshot?.FindReusableMonitor(
                        monitor.Bounds,
                        monitor.ImagePath,
                        imageIdentity);
                if (bitmap is null && previousMonitor?.Bitmap is { } previousBitmap)
                {
                    // Keep the previous pixels if this monitor still points
                    // at the same wallpaper and its file is temporarily
                    // unavailable or undecodable without a content change.
                    bitmap = previousBitmap;
                    imageIdentity ??= previousMonitor!.ImageIdentity;
                }
            }

            monitors.Add(new WallpaperMonitor(
                monitor.Bounds,
                monitor.ImagePath,
                imageIdentity,
                bitmap,
                dynamicBitmap,
                captureOptions.Source,
                captureOptions.Method,
                (imagePathWasPresent && sourceUsesStaticFallback && bitmap is null) ||
                    (captureOptions.Source == WallpaperContrastSource.DesktopHost && dynamicBitmap is null)));
        }

        if (monitors.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var snapshot = new WallpaperSnapshot(
                data.Position,
                data.BackgroundColor,
                monitors.ToArray(),
                Interlocked.Increment(ref s_snapshotVersion));
            Volatile.Write(ref s_snapshot, snapshot);
            Volatile.Write(ref s_lastRefreshError, null);
            string monitorSummary = string.Join(";", data.Monitors.Select(monitor =>
                $"{monitor.Bounds.Left:F0},{monitor.Bounds.Top:F0}-" +
                $"{monitor.Bounds.Right:F0},{monitor.Bounds.Bottom:F0}:" +
                $"{(string.IsNullOrWhiteSpace(monitor.ImagePath) ? "background" : "image" + SafeExtension(monitor.ImagePath))}"));
            int dynamicMonitorCount = monitors.Count(monitor => monitor.DynamicBitmap is not null);
            string pixelSource = dynamicMonitorCount == 0
                ? "IDesktopWallpaper"
                : dynamicMonitorCount < monitors.Count && captureOptions.Source == WallpaperContrastSource.Auto
                    ? "DesktopHostPrintWindow+IDesktopWallpaper"
                    : "DesktopHostPrintWindow";
            App.Log($"[WallpaperContrast] snapshot version={snapshot.Version}, source={pixelSource}, " +
                $"position={data.Position}, monitors={data.Monitors.Count}/{data.ReportedMonitorCount}[{monitorSummary}], " +
                $"hostCapture={hostCapture.Status}, hosts={hostCapture.HostCount}, " +
                $"attempts={hostCapture.PrintWindowAttempts}, rendered={hostCapture.RenderedWindowCount}, " +
                $"elapsedMs={hostCapture.ElapsedMilliseconds}, captures=[{hostCapture.CaptureSummary}], " +
                $"captureOptions={captureOptions.Source}/{captureOptions.Method}, " +
                $"sampling={(dynamicMonitorCount > 0 ? "screen-aligned-host-map" : captureOptions.Source == WallpaperContrastSource.DesktopHost ? "host-only-unavailable" : "static-source-or-unavailable")}");
        }
        else if (monitors.Count == 0)
        {
            Volatile.Write(ref s_lastRefreshError, "no-usable-monitors");
            App.Log($"[WallpaperContrast] refresh found no usable monitors; " +
                $"snapshot={SnapshotVersion}; retry=60s");
        }
    }

    private static WallpaperSnapshotData? ReadDesktopWallpaper()
    {
        const uint classContextAll = 0x17;
        const uint coInitializeMultithreaded = 0;
        Guid classId = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
        Guid interfaceId = new("B92B56A9-8B55-4E14-9A89-0199BBB6F93B");
        nint desktopWallpaper = 0;
        bool uninitializeCom = false;
        try
        {
            int initializeResult = FileOperationNativeMethods.CoInitializeEx(
                IntPtr.Zero,
                coInitializeMultithreaded);
            uninitializeCom = initializeResult >= 0;
            App.Log($"[WallpaperContrast] CoInitializeEx HRESULT=0x{unchecked((uint)initializeResult):X8}");
            int createResult = Ole32NativeMethods.CoCreateInstance(
                in classId,
                IntPtr.Zero,
                classContextAll,
                in interfaceId,
                out desktopWallpaper);
            if (createResult < 0 || desktopWallpaper == 0)
            {
                LogComFailure("CoCreateInstance(IDesktopWallpaper)", createResult);
                return null;
            }

            unsafe
            {
                void** vtable = *(void***)desktopWallpaper;
                int positionValue = 0;
                int positionResult = ((delegate* unmanaged[Stdcall]<nint, int*, int>)vtable[11])(
                    desktopWallpaper,
                    &positionValue);
                uint colorRef = 0;
                int colorResult = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[9])(
                    desktopWallpaper,
                    &colorRef);
                uint monitorCount = 0;
                int countResult = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[6])(
                    desktopWallpaper,
                    &monitorCount);
                if (positionResult < 0 || colorResult < 0 || countResult < 0 ||
                    positionValue is < 0 or > 5 || monitorCount is 0 or > 64)
                {
                    if (positionResult < 0) LogComFailure("GetWallpaperPosition", positionResult);
                    if (colorResult < 0) LogComFailure("GetBackgroundColor", colorResult);
                    if (countResult < 0) LogComFailure("GetMonitorDevicePathCount", countResult);
                    if (positionValue is < 0 or > 5 || monitorCount is 0 or > 64)
                    {
                        App.Log($"[WallpaperContrast] invalid COM values position={positionValue}, monitors={monitorCount}");
                    }
                    return null;
                }

                var backgroundColor = Color.FromArgb(
                    0xFF,
                    (byte)(colorRef & 0xFF),
                    (byte)((colorRef >> 8) & 0xFF),
                    (byte)((colorRef >> 16) & 0xFF));
                var monitors = new List<WallpaperMonitorData>((int)monitorCount);
                for (uint index = 0; index < monitorCount; index++)
                {
                    nint monitorId = 0;
                    int monitorResult = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[5])(
                        desktopWallpaper,
                        index,
                        &monitorId);
                    if (monitorResult < 0 || monitorId == 0)
                    {
                        if (monitorResult < 0) LogComFailure("GetMonitorDevicePathAt", monitorResult);
                        else App.Log("[WallpaperContrast] GetMonitorDevicePathAt returned a null monitor id");
                        if (monitorId != 0) FileOperationNativeMethods.CoTaskMemFree(monitorId);
                        continue;
                    }

                    try
                    {
                        NativeRect nativeRect = default;
                        int rectResult = ((delegate* unmanaged[Stdcall]<nint, nint, NativeRect*, int>)vtable[7])(
                            desktopWallpaper,
                            monitorId,
                            &nativeRect);
                        if (rectResult < 0 || nativeRect.Right <= nativeRect.Left ||
                            nativeRect.Bottom <= nativeRect.Top)
                        {
                            if (rectResult < 0) LogComFailure("GetMonitorRECT", rectResult);
                            else App.Log("[WallpaperContrast] GetMonitorRECT returned invalid bounds");
                            continue;
                        }

                        nint wallpaperPath = 0;
                        int wallpaperResult = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtable[4])(
                            desktopWallpaper,
                            monitorId,
                            &wallpaperPath);
                        if (wallpaperResult < 0)
                        {
                            LogComFailure("GetWallpaper", wallpaperResult);
                            if (wallpaperPath != 0)
                            {
                                FileOperationNativeMethods.CoTaskMemFree(wallpaperPath);
                            }

                            continue;
                        }

                        string? imagePath = ReadUtf16(wallpaperPath);
                        if (wallpaperPath != 0)
                        {
                            FileOperationNativeMethods.CoTaskMemFree(wallpaperPath);
                        }

                        monitors.Add(new WallpaperMonitorData(
                            new WallpaperContrastRect(
                                nativeRect.Left,
                                nativeRect.Top,
                                nativeRect.Right,
                                nativeRect.Bottom),
                            string.IsNullOrWhiteSpace(imagePath) ? null : imagePath));
                    }
                    finally
                    {
                        FileOperationNativeMethods.CoTaskMemFree(monitorId);
                    }
                }

                if (!WallpaperContrastPolicy.HasUsableMonitors(monitors.Count, (int)monitorCount))
                {
                    App.Log($"[WallpaperContrast] no usable monitor entries from COM enumeration reported={monitorCount}");
                    return null;
                }
                if (monitors.Count != (int)monitorCount)
                {
                    App.Log($"[WallpaperContrast] skipped unavailable COM monitors usable={monitors.Count}, reported={monitorCount}");
                }
                return new WallpaperSnapshotData(
                    (WallpaperPosition)positionValue,
                    backgroundColor,
                    monitors,
                    (int)monitorCount);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WallpaperContrast] desktop source exception={ex.GetType().Name}/" +
                $"0x{unchecked((uint)ex.HResult):X8}");
            return null;
        }
        finally
        {
            if (desktopWallpaper != 0)
            {
                unsafe
                {
                    void** vtable = *(void***)desktopWallpaper;
                    _ = ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(desktopWallpaper);
                }
            }

            if (uninitializeCom)
            {
                FileOperationNativeMethods.CoUninitialize();
            }
        }
    }

    private static unsafe string? ReadUtf16(nint value)
    {
        if (value == 0) return null;
        char* text = (char*)value;
        int length = 0;
        while (length < 32768 && text[length] != '\0') length++;
        return length == 0 ? null : new string(text, 0, length);
    }

    private static void LogComFailure(string operation, int hresult) =>
        App.Log($"[WallpaperContrast] COM {operation} failed HRESULT=0x{unchecked((uint)hresult):X8}");

    private static string SafeExtension(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            return extension.Length is > 0 and <= 8 ? extension.ToLowerInvariant() : "<unknown-extension>";
        }
        catch
        {
            return "<unknown-extension>";
        }
    }

    private static async Task<WallpaperBitmap?> GetOrDecodeBitmapAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            string? key = GetBitmapCacheKey(path);
            if (key is null || !fileInfo.Exists)
            {
                App.Log($"[WallpaperContrast] image source unavailable extension={SafeExtension(path)}, " +
                    $"exists={fileInfo.Exists}, metadata={(key is null ? "unavailable" : "available")}");
                return null;
            }
            lock (s_bitmapCache)
            {
                if (s_bitmapCache.TryGetValue(key, out CachedBitmap? cached))
                {
                    cached.LastUsedUtc = DateTime.UtcNow;
                    return cached.Bitmap;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(fileInfo.FullName);
            using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint sourceWidth = decoder.PixelWidth;
            uint sourceHeight = decoder.PixelHeight;
            if (sourceWidth == 0 || sourceHeight == 0 || sourceWidth > int.MaxValue || sourceHeight > int.MaxValue)
            {
                App.Log($"[WallpaperContrast] image decode returned invalid dimensions " +
                    $"extension={SafeExtension(path)}, width={sourceWidth}, height={sourceHeight}");
                return null;
            }

            const uint maxDimension = 192;
            double scale = Math.Min(1d, maxDimension / (double)Math.Max(sourceWidth, sourceHeight));
            uint mapWidth = Math.Max(1, (uint)Math.Round(sourceWidth * scale));
            uint mapHeight = Math.Max(1, (uint)Math.Round(sourceHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = mapWidth,
                ScaledHeight = mapHeight
            };
            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new WallpaperBitmap(
                (int)sourceWidth,
                (int)sourceHeight,
                (int)mapWidth,
                (int)mapHeight,
                pixelData.DetachPixelData());
            App.Log($"[WallpaperContrast] image decoded extension={SafeExtension(path)}, " +
                $"source={sourceWidth}x{sourceHeight}, map={mapWidth}x{mapHeight}");
            lock (s_bitmapCache)
            {
                s_bitmapCache[key] = new CachedBitmap(bitmap, DateTime.UtcNow);
                foreach (string staleKey in s_bitmapCache.Keys
                             .Where(existing => existing.StartsWith(fileInfo.FullName + "|", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    s_bitmapCache.Remove(staleKey);
                }

                while (s_bitmapCache.Count > 8)
                {
                    string oldest = s_bitmapCache.MinBy(pair => pair.Value.LastUsedUtc).Key;
                    s_bitmapCache.Remove(oldest);
                }
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[WallpaperContrast] image decode failed extension={SafeExtension(path)}, " +
                $"exception={ex.GetType().Name}/0x{unchecked((uint)ex.HResult):X8}");
            return null;
        }
    }

    private static string? GetBitmapCacheKey(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists) return null;
            return string.Concat(
                fileInfo.FullName,
                "|",
                fileInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "|",
                fileInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            return null;
        }
    }

    private static void Release()
    {
        int clients;
        bool stopped = false;
        lock (s_gate)
        {
            if (s_clients == 0) return;
            clients = --s_clients;
            if (clients == 0)
            {
                s_refreshCancellation?.Cancel();
                stopped = true;
            }
        }

        App.Log($"[WallpaperContrast] client released clients={clients}, workerCancelRequested={stopped}");
    }

    private sealed class ClientLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
        }
    }

    private sealed record CaptureWorker(
        WallpaperContrastSource Source,
        WallpaperHostCaptureMethod Method,
        WallpaperContrastRect[] MonitorBounds,
        Task<WallpaperHostCaptureResult> Task);

    private sealed class CachedBitmap(WallpaperBitmap bitmap, DateTime lastUsedUtc)
    {
        public WallpaperBitmap Bitmap { get; } = bitmap;
        public DateTime LastUsedUtc { get; set; } = lastUsedUtc;
    }

    private sealed record WallpaperBitmap(
        int SourceWidth,
        int SourceHeight,
        int MapWidth,
        int MapHeight,
        byte[] BgraPixels);

    private sealed record WallpaperMonitor(
        WallpaperContrastRect Bounds,
        string? ImagePath,
        string? ImageIdentity,
        WallpaperBitmap? Bitmap,
        WallpaperDynamicBitmap? DynamicBitmap,
        WallpaperContrastSource CaptureSource,
        WallpaperHostCaptureMethod CaptureMethod,
        bool HasUnsupportedImage);

    private sealed record WallpaperMonitorData(
        WallpaperContrastRect Bounds,
        string? ImagePath);

    private sealed record WallpaperSnapshotData(
        WallpaperPosition Position,
        Color BackgroundColor,
        IReadOnlyList<WallpaperMonitorData> Monitors,
        int ReportedMonitorCount);

    private sealed class WallpaperSnapshot(
        WallpaperPosition position,
        Color backgroundColor,
        WallpaperMonitor[] monitors,
        long version)
    {
        public long Version { get; } = version;

        private readonly WallpaperContrastRect _virtualBounds = new(
            monitors.Min(monitor => monitor.Bounds.Left),
            monitors.Min(monitor => monitor.Bounds.Top),
            monitors.Max(monitor => monitor.Bounds.Right),
            monitors.Max(monitor => monitor.Bounds.Bottom));
        private readonly double _backgroundLuminance = WallpaperContrastPolicy.SrgbLuminance(
            backgroundColor.R,
            backgroundColor.G,
            backgroundColor.B);

        public WallpaperMonitor? FindReusableMonitor(
            WallpaperContrastRect bounds,
            string? imagePath,
            string? imageIdentity)
        {
            foreach (WallpaperMonitor monitor in monitors)
            {
                if (monitor.Bitmap is not null &&
                    monitor.Bounds == bounds &&
                    string.Equals(monitor.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                    (imageIdentity is null || string.Equals(
                        monitor.ImageIdentity,
                        imageIdentity,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return monitor;
                }
            }

            return null;
        }

        public WallpaperDynamicBitmap? FindReusableDynamicMonitor(
            WallpaperContrastRect bounds,
            WallpaperContrastSource source,
            WallpaperHostCaptureMethod method)
        {
            foreach (WallpaperMonitor monitor in monitors)
            {
                if (monitor.DynamicBitmap is not null &&
                    monitor.Bounds == bounds &&
                    monitor.CaptureSource == source &&
                    monitor.CaptureMethod == method)
                {
                    return monitor.DynamicBitmap;
                }
            }

            return null;
        }

        public bool TrySampleLuminance(
            WallpaperContrastRect screenBounds,
            out double luminance)
        {
            luminance = 0;
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return false;

            double weightedLuminance = 0;
            double totalArea = 0;
            foreach (WallpaperMonitor monitor in monitors)
            {
                WallpaperContrastRect intersection = screenBounds.Intersect(monitor.Bounds);
                if (intersection.Area <= 0) continue;
                if (monitor.HasUnsupportedImage) return false;

                double average;
                if (monitor.DynamicBitmap is { } dynamicBitmap)
                {
                    if (!WallpaperHostCapturePolicy.TryAverageScreenAlignedLuminance(
                            dynamicBitmap,
                            intersection,
                            out average)) return false;
                }
                else if (monitor.Bitmap is { } bitmap)
                {
                    if (!WallpaperContrastPolicy.TryAveragePlacedLuminance(
                            bitmap.BgraPixels,
                            bitmap.MapWidth,
                            bitmap.MapHeight,
                            bitmap.SourceWidth,
                            bitmap.SourceHeight,
                            position,
                            monitor.Bounds,
                            _virtualBounds,
                            intersection,
                            _backgroundLuminance,
                            out average)) return false;
                }
                else
                {
                    average = _backgroundLuminance;
                }

                weightedLuminance += average * intersection.Area;
                totalArea += intersection.Area;
            }

            if (totalArea <= 0) return false;
            luminance = weightedLuminance / totalArea;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
