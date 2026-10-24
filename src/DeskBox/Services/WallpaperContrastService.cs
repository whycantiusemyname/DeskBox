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
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, CachedBitmap> s_bitmapCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static WallpaperSnapshot? s_snapshot;
    private static long s_snapshotVersion;
    private static CancellationTokenSource? s_refreshCancellation;
    private static Task? s_refreshWorker;
    private static int s_clients;

    public static IDisposable Acquire()
    {
        lock (s_gate)
        {
            s_clients++;
            if (s_clients == 1)
            {
                s_refreshCancellation = new CancellationTokenSource();
                CancellationToken token = s_refreshCancellation.Token;
                s_refreshWorker = Task.Run(() => RefreshLoopAsync(token));
            }
        }

        return new ClientLease();
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
            await TryRefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
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
        catch
        {
            // A transient COM, file, or decoder failure preserves the last
            // usable map and leaves the independent 60-second retry alive.
        }
    }

    private static async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        WallpaperSnapshotData? data = await Task.Run(
            ReadDesktopWallpaper,
            cancellationToken).ConfigureAwait(false);
        if (data is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        WallpaperSnapshot? previousSnapshot = Volatile.Read(ref s_snapshot);
        var monitors = new List<WallpaperMonitor>(data.Monitors.Count);
        foreach (WallpaperMonitorData monitor in data.Monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WallpaperBitmap? bitmap = null;
            bool imagePathWasPresent = !string.IsNullOrWhiteSpace(monitor.ImagePath);
            string? imageIdentity = imagePathWasPresent
                ? GetBitmapCacheKey(monitor.ImagePath!)
                : null;
            if (imagePathWasPresent)
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
                imagePathWasPresent && bitmap is null));
        }

        if (monitors.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref s_snapshot, new WallpaperSnapshot(
                data.Position,
                data.BackgroundColor,
                monitors.ToArray(),
                Interlocked.Increment(ref s_snapshotVersion)));
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
            int createResult = Ole32NativeMethods.CoCreateInstance(
                in classId,
                IntPtr.Zero,
                classContextAll,
                in interfaceId,
                out desktopWallpaper);
            if (createResult < 0 || desktopWallpaper == 0)
            {
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
                        if (monitorId != 0) FileOperationNativeMethods.CoTaskMemFree(monitorId);
                        return null;
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
                            return null;
                        }

                        nint wallpaperPath = 0;
                        int wallpaperResult = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtable[4])(
                            desktopWallpaper,
                            monitorId,
                            &wallpaperPath);
                        if (wallpaperResult < 0)
                        {
                            if (wallpaperPath != 0)
                            {
                                FileOperationNativeMethods.CoTaskMemFree(wallpaperPath);
                            }

                            return null;
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

                if (monitors.Count != (int)monitorCount) return null;
                return new WallpaperSnapshotData(
                    (WallpaperPosition)positionValue,
                    backgroundColor,
                    monitors);
            }
        }
        catch
        {
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

    private static async Task<WallpaperBitmap?> GetOrDecodeBitmapAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            string? key = GetBitmapCacheKey(path);
            if (key is null || !fileInfo.Exists) return null;
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
        catch
        {
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
        lock (s_gate)
        {
            if (s_clients == 0 || --s_clients != 0) return;
            s_refreshCancellation?.Cancel();
        }
    }

    private sealed class ClientLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
        }
    }

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
        bool HasUnsupportedImage);

    private sealed record WallpaperMonitorData(
        WallpaperContrastRect Bounds,
        string? ImagePath);

    private sealed record WallpaperSnapshotData(
        WallpaperPosition Position,
        Color BackgroundColor,
        IReadOnlyList<WallpaperMonitorData> Monitors);

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
                if (monitor.Bitmap is { } bitmap)
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
