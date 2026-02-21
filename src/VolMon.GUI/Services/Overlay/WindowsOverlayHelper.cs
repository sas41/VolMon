using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// Windows overlay helper. Avalonia's built-in Topmost, ShowInTaskbar, and
/// CanFocus properties handle most overlay requirements natively on Windows.
/// The only platform-specific call is GetCursorPos for multi-monitor detection.
///
/// Note: This works for borderless fullscreen windows. True exclusive fullscreen
/// (DirectX/Vulkan exclusive mode) cannot be overlaid without hooking the
/// graphics API, which is out of scope.
/// </summary>
public sealed class WindowsOverlayHelper : IPlatformOverlayHelper
{
    // ── Win32 P/Invoke — cursor position only ────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ── IPlatformOverlayHelper ───────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// No-op on Windows. Avalonia's Topmost=true maps to HWND_TOPMOST natively,
    /// and ShowInTaskbar=false / non-focusable are handled by the XAML properties.
    /// </remarks>
    public void ApplyOverlayHints(Window window)
    {
        // Avalonia handles Topmost, ShowInTaskbar, and focus natively on Windows.
    }

    /// <inheritdoc />
    public void RaiseToTop(Window window)
    {
        // Toggling Topmost forces Avalonia to re-apply HWND_TOPMOST via SetWindowPos.
        window.Topmost = false;
        window.Topmost = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns null — Windows doesn't need compositor-specific output name
    /// detection. Cursor-based detection via <see cref="FindScreenAtCursor"/>
    /// is sufficient.
    /// </remarks>
    public string? GetActiveOutputName() => null;

    /// <inheritdoc />
    /// <remarks>
    /// Uses Win32 GetCursorPos to get the cursor position in screen coordinates,
    /// then matches against Avalonia's screens by bounds containment.
    /// This is the only platform-specific call needed — Avalonia does not expose
    /// a cross-platform API to query the global cursor position on demand.
    /// </remarks>
    public Screen? FindScreenAtCursor(Screens screens)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        try
        {
            if (!GetCursorPos(out var pt)) return null;

            var cursor = new PixelPoint(pt.X, pt.Y);

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
        // No warm-up needed on Windows — Avalonia handles everything natively.
        onReady();
    }
}
