using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// macOS overlay helper. Uses Objective-C runtime P/Invoke for two things
/// Avalonia doesn't expose natively:
///   1. NSWindow.level + collectionBehavior — needed to render above fullscreen apps
///   2. CGEventGetLocation — needed to query global cursor position for multi-monitor
///
/// Everything else (topmost, no-taskbar, non-focusable) is handled by Avalonia.
/// </summary>
public sealed class MacOsOverlayHelper : IPlatformOverlayHelper
{
    // ── Objective-C Runtime P/Invoke (minimal set) ───────────────────

    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(ObjCRuntime, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_ulong(IntPtr receiver, IntPtr selector, ulong arg);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern long objc_msgSend_retLong(IntPtr receiver, IntPtr selector);

    // ── CoreGraphics P/Invoke (cursor position) ─────────────────────

    [DllImport(CoreGraphicsLib)]
    private static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphicsLib)]
    private static extern void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    // NSWindow.level: ScreenSaver level renders above fullscreen apps
    private const long kCGScreenSaverWindowLevel = 1000;

    // NSWindowCollectionBehavior flags
    private const ulong NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0;
    private const ulong NSWindowCollectionBehaviorFullScreenAuxiliary = 1 << 8;
    private const ulong NSWindowCollectionBehaviorStationary = 1 << 4;
    private const ulong NSWindowCollectionBehaviorIgnoresCycle = 1 << 6;

    // ── IPlatformOverlayHelper ───────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Sets NSWindow.level to kCGScreenSaverWindowLevel (above fullscreen apps)
    /// and configures collectionBehavior so the overlay appears on all Mission
    /// Control spaces and alongside fullscreen apps. Avalonia's Topmost maps to
    /// NSWindow.level = floating (3), which is not high enough for fullscreen.
    /// </remarks>
    public void ApplyOverlayHints(Window window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        try
        {
            var nsWindow = GetNSWindow(window);
            if (nsWindow == IntPtr.Zero) return;

            // Set window level above fullscreen apps
            objc_msgSend_long(nsWindow, sel_registerName("setLevel:"), kCGScreenSaverWindowLevel);

            // Join all spaces + appear alongside fullscreen apps + don't appear in Expose/Cmd-Tab
            objc_msgSend_ulong(nsWindow, sel_registerName("setCollectionBehavior:"),
                NSWindowCollectionBehaviorCanJoinAllSpaces |
                NSWindowCollectionBehaviorFullScreenAuxiliary |
                NSWindowCollectionBehaviorStationary |
                NSWindowCollectionBehaviorIgnoresCycle);

            // Don't auto-hide when the app deactivates (e.g. game takes focus)
            objc_msgSend_long(nsWindow, sel_registerName("setHidesOnDeactivate:"), 0);
        }
        catch
        {
            // Silently ignore — fall back to Avalonia's built-in Topmost
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses orderFrontRegardless to bring the window to front without requiring
    /// the app to be active — critical when a fullscreen game is in front.
    /// </remarks>
    public void RaiseToTop(Window window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        try
        {
            var nsWindow = GetNSWindow(window);
            if (nsWindow == IntPtr.Zero)
            {
                window.Topmost = true;
                return;
            }

            objc_msgSend_void(nsWindow, sel_registerName("orderFrontRegardless"));
        }
        catch
        {
            window.Topmost = true;
        }
    }

    /// <inheritdoc />
    public string? GetActiveOutputName() => null;

    /// <inheritdoc />
    /// <remarks>
    /// Uses CoreGraphics CGEventGetLocation to query the global cursor position,
    /// then matches against Avalonia's screens. Avalonia does not expose a
    /// cross-platform API for on-demand cursor position queries.
    /// </remarks>
    public Screen? FindScreenAtCursor(Screens screens)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return null;

        try
        {
            // CGEventCreate(NULL) + CGEventGetLocation gives us the cursor
            // position in global display coordinates (origin at top-left of
            // primary display).
            var eventRef = CGEventCreate(IntPtr.Zero);
            if (eventRef == IntPtr.Zero) return null;

            CGPoint location;
            try
            {
                location = CGEventGetLocation(eventRef);
            }
            finally
            {
                CFRelease(eventRef);
            }

            var cursor = new PixelPoint((int)location.X, (int)location.Y);

            foreach (var screen in screens.All)
            {
                if (screen.Bounds.Contains(cursor))
                    return screen;
            }
        }
        catch
        {
            // Fall through
        }

        return null;
    }

    /// <inheritdoc />
    public void WarmUp(Window window, Action onReady)
    {
        // Show briefly off-screen to get a platform handle, apply hints, then hide.
        // Needed so ApplyOverlayHints can access the NSWindow on first ShowOverlay().
        window.Position = new PixelPoint(-10000, -10000);
        window.Show();

        DispatcherTimer.RunOnce(() =>
        {
            ApplyOverlayHints(window);
            onReady();
            window.Hide();
        }, TimeSpan.FromMilliseconds(100));
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static IntPtr GetNSWindow(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null) return IntPtr.Zero;

        if (handle.HandleDescriptor == "NSWindow")
            return handle.Handle;

        // Avalonia may return an NSView — get its parent window
        if (handle.HandleDescriptor == "NSView" && handle.Handle != IntPtr.Zero)
            return (IntPtr)objc_msgSend_retLong(handle.Handle, sel_registerName("window"));

        return IntPtr.Zero;
    }
}
