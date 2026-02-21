using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// Base implementation for Linux X11/XWayland overlay helpers. Provides shared
/// X11 P/Invoke operations: override-redirect, raise, and XQueryPointer.
///
/// Subclasses provide the compositor-specific active output detection
/// (KDE via KWin D-Bus, GNOME via Shell eval, etc.).
/// </summary>
public abstract class X11OverlayBase : IPlatformOverlayHelper
{
    // ── Xlib P/Invoke ────────────────────────────────────────────────

    private const string Xlib = "libX11.so.6";

    [DllImport(Xlib)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(Xlib)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(Xlib)]
    private static extern int XFlush(IntPtr display);

    [DllImport(Xlib)]
    private static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport(Xlib)]
    private static extern int XChangeWindowAttributes(
        IntPtr display, IntPtr window, ulong valueMask, ref XSetWindowAttributes attributes);

    [DllImport(Xlib)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(Xlib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XQueryPointer(
        IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY,
        out int winX, out int winY,
        out uint maskReturn);

    [DllImport(Xlib)]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(Xlib)]
    private static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, IntPtr[] data, int nelements);

    private const ulong CWOverrideRedirect = 1 << 9;
    private const int PropModeReplace = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public ulong background_pixel;
        public IntPtr border_pixmap;
        public ulong border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public ulong backing_planes;
        public ulong backing_pixel;
        public int save_under;
        public long event_mask;
        public long do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
        public IntPtr cursor;
    }

    // ── IPlatformOverlayHelper ───────────────────────────────────────

    /// <inheritdoc />
    public void ApplyOverlayHints(Window window)
    {
        try
        {
            var xid = GetXid(window);
            if (xid == IntPtr.Zero) return;

            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;

            try
            {
                // Set override_redirect = true — bypasses the window manager
                var attrs = new XSetWindowAttributes { override_redirect = 1 };
                XChangeWindowAttributes(display, xid, CWOverrideRedirect, ref attrs);

                // Set _NET_WM_WINDOW_TYPE to NOTIFICATION as a secondary hint
                var wmWindowType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
                var typeNotification = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
                var atom = XInternAtom(display, "ATOM", false);
                XChangeProperty(display, xid, wmWindowType, atom, 32, PropModeReplace,
                    [typeNotification], 1);

                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // Non-X11 or failure — silently ignore
        }
    }

    /// <inheritdoc />
    public void RaiseToTop(Window window)
    {
        try
        {
            var xid = GetXid(window);
            if (xid == IntPtr.Zero) return;

            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;

            try
            {
                XRaiseWindow(display, xid);
                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // Silently ignore
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Subclasses must implement this to query the compositor for the active output.
    /// </remarks>
    public abstract string? GetActiveOutputName();

    /// <inheritdoc />
    public Screen? FindScreenAtCursor(Screens screens)
    {
        try
        {
            var pos = QueryPointerPosition();
            if (pos is null) return null;

            foreach (var screen in screens.All)
            {
                if (screen.Bounds.Contains(pos.Value))
                    return screen;
            }
        }
        catch { /* fall through */ }

        return null;
    }

    /// <inheritdoc />
    public void WarmUp(Window window, Action onReady)
    {
        // Show briefly off-screen, apply override-redirect, then hide.
        window.Position = new PixelPoint(-10000, -10000);
        window.Show();

        DispatcherTimer.RunOnce(() =>
        {
            onReady();
            window.Hide();
        }, TimeSpan.FromMilliseconds(200));
    }

    // ── Protected helpers ────────────────────────────────────────────

    /// <summary>
    /// Queries the cursor position via XQueryPointer. Returns coordinates in
    /// X11 root (physical pixel) space, or null on failure.
    /// </summary>
    protected static PixelPoint? QueryPointerPosition()
    {
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null;

            try
            {
                var root = XDefaultRootWindow(display);
                if (XQueryPointer(display, root,
                        out _, out _, out int rootX, out int rootY, out _, out _, out _))
                {
                    return new PixelPoint(rootX, rootY);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch { /* non-X11 */ }

        return null;
    }

    private static IntPtr GetXid(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "XID") return IntPtr.Zero;
        return handle.Handle;
    }
}
