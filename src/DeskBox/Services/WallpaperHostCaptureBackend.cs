using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Platform;

namespace DeskBox.Services;

internal enum WallpaperContrastSource
{
    Auto,
    DesktopHost,
    SystemWallpaper
}

internal enum WallpaperHostCaptureMethod
{
    Auto,
    FullWindow,
    LegacyWindow
}

internal sealed record WallpaperCaptureOptions(
    WallpaperContrastSource Source = WallpaperContrastSource.Auto,
    WallpaperHostCaptureMethod Method = WallpaperHostCaptureMethod.Auto,
    int RefreshIntervalSeconds = 60)
{
    public WallpaperCaptureOptions Normalize() => this with
    {
        Source = Enum.IsDefined(typeof(WallpaperContrastSource), Source) ? Source : WallpaperContrastSource.Auto,
        Method = Enum.IsDefined(typeof(WallpaperHostCaptureMethod), Method) ? Method : WallpaperHostCaptureMethod.Auto,
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 1, 3600)
    };
}

/// <summary>
/// Captures structurally hosted desktop wallpaper windows. Discovery is based
/// on the Explorer desktop tree rather than a wallpaper product/process name.
/// </summary>
internal static class WallpaperHostCaptureBackend
{
    private const int MaxTreeNodes = 4096;
    private const int MaxCaptureCandidates = 48;
    private const int MaxCaptureDimension = 8192;
    private const long MaxCapturePixels = 16_777_216;
    private const int MaxMapDimension = 192;
    private const uint PrintWindowRenderFullContent = 2;

    public static WallpaperHostCaptureResult Capture(
        IReadOnlyList<WallpaperContrastRect> monitorBounds,
        WallpaperHostCaptureMethod method)
    {
        var stopwatch = Stopwatch.StartNew();
        List<WallpaperHostWindowCandidate> candidates = EnumerateCandidates(
            monitorBounds,
            out bool enumerationSucceeded);
        if (candidates.Count == 0)
        {
            stopwatch.Stop();
            return new WallpaperHostCaptureResult(
                enumerationSucceeded ? "no-host" : "enumeration-failed",
                new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>(),
                0,
                0,
                0,
                stopwatch.ElapsedMilliseconds,
                string.Empty);
        }

        var monitorCandidates = new Dictionary<WallpaperContrastRect, List<WallpaperHostWindowCandidate>>();
        foreach (WallpaperContrastRect monitor in monitorBounds)
        {
            List<WallpaperHostWindowCandidate> ordered = candidates
                .Where(candidate => IsCandidateForMonitor(candidate, monitor))
                .OrderBy(candidate => GetCandidatePriority(candidate, monitor, candidates))
                .ThenBy(candidate => candidate.Bounds.Area)
                .ThenByDescending(candidate => candidate.Depth)
                .ToList();
            monitorCandidates[monitor] = ordered;
        }

        int hostCount = candidates.Count;
        var maps = new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>();
        var attempted = new HashSet<IntPtr>();
        var captures = new List<string>();
        int printWindowAttempts = 0;
        int renderedWindowCount = 0;

        while (maps.Count < monitorCandidates.Count &&
               printWindowAttempts < MaxCaptureCandidates)
        {
            WallpaperHostWindowCandidate? next = monitorCandidates
                .Where(pair => !maps.ContainsKey(pair.Key))
                .SelectMany(pair => pair.Value)
                .Where(candidate => !attempted.Contains(candidate.Handle))
                .OrderBy(candidate => monitorCandidates
                    .Where(pair => !maps.ContainsKey(pair.Key) && pair.Value.Contains(candidate))
                    .Min(pair => GetCandidatePriority(candidate, pair.Key, candidates)))
                .ThenBy(candidate => candidate.Bounds.Area)
                .ThenByDescending(candidate => candidate.Depth)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            attempted.Add(next.Handle);
            printWindowAttempts++;
            if (!TryCaptureWindow(
                    next,
                    monitorBounds,
                    method,
                    out Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap> capturedByMonitor,
                    out string captureMethodUsed))
            {
                continue;
            }

            renderedWindowCount++;
            int mappedCount = 0;
            foreach (WallpaperContrastRect monitor in monitorBounds)
            {
                if (maps.ContainsKey(monitor) ||
                    !monitorCandidates.TryGetValue(monitor, out List<WallpaperHostWindowCandidate>? options) ||
                    !options.Contains(next) ||
                    !capturedByMonitor.TryGetValue(
                        monitor,
                        out WallpaperDynamicBitmap? map) ||
                    map is null)
                {
                    continue;
                }

                maps[monitor] = map;
                mappedCount++;
            }

            if (mappedCount > 0 && captures.Count < 16)
            {
                captures.Add($"{next.ClassName}@{FormatRect(next.Bounds)}:{mappedCount}:{captureMethodUsed}");
            }
        }

        string status = maps.Count > 0 ? "captured" : "capture-failed";
        stopwatch.Stop();
        return new WallpaperHostCaptureResult(
            status,
            maps,
            hostCount,
            printWindowAttempts,
            renderedWindowCount,
            stopwatch.ElapsedMilliseconds,
            string.Join(";", captures));
    }

    private static List<WallpaperHostWindowCandidate> EnumerateCandidates(
        IReadOnlyList<WallpaperContrastRect> monitorBounds,
        out bool enumerationSucceeded)
    {
        var topLevelWindows = new List<IntPtr>();
        bool complete = Win32Helper.EnumWindows((window, _) =>
        {
            if (topLevelWindows.Count >= MaxTreeNodes)
            {
                return false;
            }

            topLevelWindows.Add(window);
            return true;
        }, IntPtr.Zero);
        enumerationSucceeded = complete || topLevelWindows.Count >= MaxTreeNodes;

        var candidates = new List<WallpaperHostWindowCandidate>();
        var visited = new HashSet<IntPtr>();
        int visitedCount = 0;
        foreach (IntPtr root in topLevelWindows)
        {
            string rootClass = GetClassName(root);
            if (!IsDesktopRootClass(rootClass))
            {
                continue;
            }

            _ = VisitWindowTree(
                root,
                rootClass,
                parentClass: null,
                depth: 0,
                ancestors: Array.Empty<IntPtr>(),
                ancestorsAreVisible: true,
                isInDesktopShellTree: true,
                isInWorkerWBranch: string.Equals(rootClass, "WorkerW", StringComparison.OrdinalIgnoreCase),
                isInIconHostBranch: false,
                monitorBounds,
                candidates,
                visited,
                ref visitedCount);
            if (visitedCount >= MaxTreeNodes)
            {
                break;
            }
        }

        return candidates;
    }

    private static WindowTreeSummary VisitWindowTree(
        IntPtr window,
        string rootClass,
        string? parentClass,
        int depth,
        IReadOnlyList<IntPtr> ancestors,
        bool ancestorsAreVisible,
        bool isInDesktopShellTree,
        bool isInWorkerWBranch,
        bool isInIconHostBranch,
        IReadOnlyList<WallpaperContrastRect> monitorBounds,
        ICollection<WallpaperHostWindowCandidate> candidates,
        ISet<IntPtr> visited,
        ref int visitedCount)
    {
        if (window == IntPtr.Zero || depth > 32 || visitedCount >= MaxTreeNodes || !visited.Add(window))
        {
            return default;
        }

        visitedCount++;
        string className = GetClassName(window);
        bool isIconHost = WallpaperHostCapturePolicy.IsIconHostClass(className);
        bool inIconBranch = isInIconHostBranch || isIconHost;
        bool inWorkerWBranch = isInWorkerWBranch ||
            string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase);
        bool isVisible = ancestorsAreVisible && Win32Helper.IsWindowVisible(window);
        Win32Helper.GetWindowThreadProcessId(window, out uint processId);
        bool containsDeskBoxWindow = processId == (uint)Environment.ProcessId;
        bool containsIconHost = isIconHost;

        var childSummaries = new List<WindowTreeSummary>();
        var childAncestors = new IntPtr[ancestors.Count + 1];
        for (int index = 0; index < ancestors.Count; index++)
        {
            childAncestors[index] = ancestors[index];
        }

        childAncestors[^1] = window;
        if (!isIconHost && !containsDeskBoxWindow && visitedCount < MaxTreeNodes)
        {
            IntPtr child = IntPtr.Zero;
            while (visitedCount < MaxTreeNodes)
            {
                child = Win32Helper.FindWindowEx(window, child, null, null);
                if (child == IntPtr.Zero)
                {
                    break;
                }

                WindowTreeSummary summary = VisitWindowTree(
                    child,
                    rootClass,
                    className,
                    depth + 1,
                    childAncestors,
                    isVisible,
                    isInDesktopShellTree,
                    inWorkerWBranch,
                    inIconBranch,
                    monitorBounds,
                    candidates,
                    visited,
                    ref visitedCount);
                childSummaries.Add(summary);
                containsIconHost |= summary.ContainsIconHost;
                containsDeskBoxWindow |= summary.ContainsDeskBoxWindow;
            }
        }

        WallpaperHostWindowCandidate candidate = new(
            window,
            className,
            rootClass,
            parentClass,
            depth,
            ancestors.ToArray(),
            GetWindowBounds(window),
            processId,
            isVisible,
            inWorkerWBranch,
            isInDesktopShellTree,
            inIconBranch,
            containsIconHost && !isIconHost,
            containsDeskBoxWindow && processId != (uint)Environment.ProcessId);
        if (monitorBounds.Any(monitor => IsCandidateForMonitor(candidate, monitor)))
        {
            candidates.Add(candidate);
        }

        bool containsWallpaperRenderer = childSummaries.Any(summary => summary.ContainsWallpaperRenderer);
        bool isWallpaperRenderer =
            !inIconBranch &&
            !containsDeskBoxWindow &&
            !IsDesktopRootClass(className) &&
            monitorBounds.Any(monitor => WallpaperHostCapturePolicy.IsEligibleCandidate(
                className,
                candidate.Bounds,
                monitor,
                isVisible,
                isInDesktopShellTree,
                isInIconHostBranch: false,
                hasIconHostDescendant: containsIconHost,
                hasDeskBoxDescendant: false,
                processId,
                (uint)Environment.ProcessId));

        return new WindowTreeSummary(
            containsIconHost,
            containsDeskBoxWindow,
            containsWallpaperRenderer || isWallpaperRenderer);
    }

    private static bool IsCandidateForMonitor(
        WallpaperHostWindowCandidate candidate,
        WallpaperContrastRect monitor) =>
        WallpaperHostCapturePolicy.IsEligibleCandidate(
            candidate.ClassName,
            candidate.Bounds,
            monitor,
            candidate.IsVisible,
            candidate.IsInDesktopShellTree,
            candidate.IsInIconHostBranch,
            candidate.HasIconHostDescendant,
            candidate.HasDeskBoxDescendant,
            candidate.ProcessId,
            (uint)Environment.ProcessId);

    private static int GetCandidatePriority(
        WallpaperHostWindowCandidate candidate,
        WallpaperContrastRect monitor,
        IReadOnlyList<WallpaperHostWindowCandidate> candidates)
    {
        bool isWorkerW = string.Equals(candidate.ClassName, "WorkerW", StringComparison.OrdinalIgnoreCase);
        bool hasRendererDescendant = candidates.Any(other =>
            other.AncestorHandles.Contains(candidate.Handle) &&
            other.IsInWorkerWBranch &&
            IsCandidateForMonitor(other, monitor));
        if (isWorkerW && hasRendererDescendant)
        {
            // This structural parent has produced a complete desktop frame on
            // current Windows dynamic wallpaper hosts; retain descendants as
            // a generic fallback when PrintWindow cannot render the parent.
            return 0;
        }

        bool isDirectProgmanChild =
            string.Equals(candidate.RootClassName, "Progman", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ParentClassName, "Progman", StringComparison.OrdinalIgnoreCase) &&
            !isWorkerW;
        if (isDirectProgmanChild)
        {
            // Some current engines insert a layered desktop renderer directly
            // under Progman between the icon layer and WorkerW.
            return 1;
        }

        if (isWorkerW)
        {
            return 2;
        }

        if (candidate.IsInWorkerWBranch)
        {
            return 3;
        }

        return string.Equals(candidate.ClassName, "Progman", StringComparison.OrdinalIgnoreCase)
            ? 5
            : 4;
    }

    private static bool TryCaptureWindow(
        WallpaperHostWindowCandidate candidate,
        IReadOnlyList<WallpaperContrastRect> monitorBounds,
        WallpaperHostCaptureMethod method,
        out Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap> mapsByMonitor,
        out string captureMethodUsed)
    {
        mapsByMonitor = new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>();
        captureMethodUsed = "unavailable";
        if (!IsCandidateStillVisibleAndUnchanged(candidate))
        {
            return false;
        }

        WallpaperContrastRect bounds = candidate.Bounds;
        int width = (int)bounds.Width;
        int height = (int)bounds.Height;
        long pixelCount = (long)width * height;
        if (width <= 0 || height <= 0 ||
            width > MaxCaptureDimension || height > MaxCaptureDimension ||
            pixelCount <= 0 || pixelCount > MaxCapturePixels)
        {
            return false;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr sourceWindowDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memoryDc = WallpaperCaptureNativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            var bitmapInfo = new WallpaperCaptureNativeMethods.BitmapInfo
            {
                Header = new WallpaperCaptureNativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<WallpaperCaptureNativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = checked((uint)(pixelCount * 4))
                }
            };
            bitmap = WallpaperCaptureNativeMethods.CreateDIBSection(
                memoryDc,
                in bitmapInfo,
                dibColorModeRgb: 0,
                out IntPtr pixels,
                IntPtr.Zero,
                0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                new Span<byte>((void*)pixels, checked((int)(pixelCount * 4))).Clear();
            }

            oldBitmap = WallpaperCaptureNativeMethods.SelectObject(memoryDc, bitmap);
            if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1))
            {
                oldBitmap = IntPtr.Zero;
                return false;
            }

            bool captured;
            switch (method)
            {
                case WallpaperHostCaptureMethod.FullWindow:
                    captureMethodUsed = "PrintWindow(flags=2)";
                    captured = PrintFullWindow(candidate.Handle, memoryDc);
                    break;
                case WallpaperHostCaptureMethod.LegacyWindow:
                    captureMethodUsed = "GetWindowDC+BitBlt";
                    captured = BlitWindow(candidate.Handle, memoryDc, width, height, out sourceWindowDc);
                    break;
                default:
                    captureMethodUsed = "PrintWindow(flags=2)";
                    captured = PrintFullWindow(candidate.Handle, memoryDc);
                    if (!captured)
                    {
                        captureMethodUsed = "GetWindowDC+BitBlt";
                        captured = BlitWindow(candidate.Handle, memoryDc, width, height, out sourceWindowDc);
                    }

                    break;
            }
            if (!captured)
            {
                return false;
            }

            if (!WallpaperCaptureNativeMethods.GdiFlush())
            {
                App.Log($"[WallpaperContrast] GdiFlush failed hostClass={candidate.ClassName} rect={FormatRect(bounds)} method={captureMethodUsed}");
                captureMethodUsed = "GdiFlush-failed";
                return false;
            }

            unsafe
            {
                var source = new ReadOnlySpan<byte>((void*)pixels, checked((int)(pixelCount * 4)));
                foreach (WallpaperContrastRect monitor in monitorBounds)
                {
                    if (!IsCandidateForMonitor(candidate, monitor))
                    {
                        continue;
                    }

                    if (!WallpaperHostCapturePolicy.TryCreateScreenAlignedMap(
                            source,
                            width,
                            height,
                            bounds,
                            monitor,
                            MaxMapDimension,
                            out WallpaperDynamicBitmap? map) || map is null)
                    {
                        continue;
                    }

                    mapsByMonitor[monitor] = map;
                }
            }

            return mapsByMonitor.Count > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (sourceWindowDc != IntPtr.Zero)
            {
                _ = WallpaperCaptureNativeMethods.ReleaseDC(candidate.Handle, sourceWindowDc);
            }

            if (memoryDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                _ = WallpaperCaptureNativeMethods.SelectObject(memoryDc, oldBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = WallpaperCaptureNativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = WallpaperCaptureNativeMethods.DeleteDC(memoryDc);
            }
        }
    }

    private static bool PrintFullWindow(IntPtr window, IntPtr destinationDc) =>
        WallpaperCaptureNativeMethods.PrintWindow(
            window,
            destinationDc,
            PrintWindowRenderFullContent);

    private static bool BlitWindow(
        IntPtr window,
        IntPtr destinationDc,
        int width,
        int height,
        out IntPtr sourceWindowDc)
    {
        sourceWindowDc = WallpaperCaptureNativeMethods.GetWindowDC(window);
        return sourceWindowDc != IntPtr.Zero &&
               WallpaperCaptureNativeMethods.BitBlt(
                   destinationDc,
                   0,
                   0,
                   width,
                   height,
                   sourceWindowDc,
                   0,
                   0,
                   0x00CC0020);
    }

    private static WallpaperContrastRect GetWindowBounds(IntPtr window)
    {
        if (!Win32Helper.GetWindowRect(window, out Win32Helper.RECT rect) ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return default;
        }

        return new WallpaperContrastRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static bool IsCandidateStillVisibleAndUnchanged(WallpaperHostWindowCandidate candidate)
    {
        if (!candidate.IsVisible || !Win32Helper.IsWindowVisible(candidate.Handle) ||
            GetWindowBounds(candidate.Handle) != candidate.Bounds ||
            !string.Equals(GetClassName(candidate.Handle), candidate.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(candidate.Handle, out uint processId);
        if (processId != candidate.ProcessId)
        {
            return false;
        }

        foreach (IntPtr ancestor in candidate.AncestorHandles)
        {
            if (!Win32Helper.IsWindowVisible(ancestor))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        return Win32Helper.GetClassName(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static bool IsDesktopRootClass(string className) =>
        string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase);

    private static string FormatRect(WallpaperContrastRect bounds) =>
        $"{bounds.Left:0},{bounds.Top:0},{bounds.Width:0}x{bounds.Height:0}";

    private sealed record WallpaperHostWindowCandidate(
        IntPtr Handle,
        string ClassName,
        string RootClassName,
        string? ParentClassName,
        int Depth,
        IntPtr[] AncestorHandles,
        WallpaperContrastRect Bounds,
        uint ProcessId,
        bool IsVisible,
        bool IsInWorkerWBranch,
        bool IsInDesktopShellTree,
        bool IsInIconHostBranch,
        bool HasIconHostDescendant,
        bool HasDeskBoxDescendant);

    private readonly record struct WindowTreeSummary(
        bool ContainsIconHost,
        bool ContainsDeskBoxWindow,
        bool ContainsWallpaperRenderer);
}

internal sealed record WallpaperHostCaptureResult(
    string Status,
    IReadOnlyDictionary<WallpaperContrastRect, WallpaperDynamicBitmap> Maps,
    int HostCount,
    int PrintWindowAttempts,
    int RenderedWindowCount,
    long ElapsedMilliseconds,
    string CaptureSummary)
{
    public static WallpaperHostCaptureResult Disabled { get; } = new(
        "disabled",
        new Dictionary<WallpaperContrastRect, WallpaperDynamicBitmap>(),
        0,
        0,
        0,
        0,
        string.Empty);
}
