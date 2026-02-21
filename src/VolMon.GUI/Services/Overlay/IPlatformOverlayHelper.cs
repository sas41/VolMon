using Avalonia.Controls;
using Avalonia.Platform;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// Platform-specific operations for the overlay window.
/// Implementations handle making the overlay render above fullscreen windows,
/// raising it in the stacking order, and detecting which monitor is active.
/// </summary>
public interface IPlatformOverlayHelper
{
    /// <summary>
    /// Apply platform-specific hints to make the window render above all other
    /// windows, including fullscreen/borderless games. Called after the window
    /// has been shown (mapped) and has a platform handle.
    /// </summary>
    void ApplyOverlayHints(Window window);

    /// <summary>
    /// Raise the window to the top of the stacking order.
    /// Called every time the overlay is shown.
    /// </summary>
    void RaiseToTop(Window window);

    /// <summary>
    /// Returns the display name of the monitor the cursor is currently on,
    /// matching Avalonia's <see cref="Screen.DisplayName"/>.
    /// Returns null if it cannot be determined.
    /// </summary>
    string? GetActiveOutputName();

    /// <summary>
    /// Finds the screen the cursor is on by matching the cursor position
    /// against the given screen list. Returns null if it cannot be determined.
    /// </summary>
    Screen? FindScreenAtCursor(Screens screens);

    /// <summary>
    /// Called once at startup to pre-warm the overlay window (e.g. briefly
    /// show it off-screen so it gets a platform handle). The implementation
    /// should call <paramref name="onReady"/> when the warm-up is complete.
    /// </summary>
    void WarmUp(Window window, Action onReady);
}
