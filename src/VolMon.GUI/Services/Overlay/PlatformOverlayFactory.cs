using System.Runtime.InteropServices;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// Detects the current platform and desktop environment at startup and returns
/// the appropriate <see cref="IPlatformOverlayHelper"/> implementation.
/// </summary>
public static class PlatformOverlayFactory
{
    /// <summary>
    /// Creates the platform-specific overlay helper for the current environment.
    /// Detection order for Linux: KDE → GNOME → fallback to KDE (most common).
    /// </summary>
    public static IPlatformOverlayHelper Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsOverlayHelper();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsOverlayHelper();

        // Linux: detect desktop environment
        return DetectLinuxDesktop();
    }

    private static IPlatformOverlayHelper DetectLinuxDesktop()
    {
        // XDG_CURRENT_DESKTOP is the most reliable indicator.
        // Examples: "KDE", "GNOME", "GNOME:GNOME", "ubuntu:GNOME", "X-Cinnamon"
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        var desktopLower = desktop.ToLowerInvariant();

        if (desktopLower.Contains("kde") || desktopLower.Contains("plasma"))
            return new KdeOverlayHelper();

        if (desktopLower.Contains("gnome") || desktopLower.Contains("unity"))
            return new GnomeOverlayHelper();

        // DESKTOP_SESSION as a secondary check
        var session = Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "";
        var sessionLower = session.ToLowerInvariant();

        if (sessionLower.Contains("kde") || sessionLower.Contains("plasma"))
            return new KdeOverlayHelper();

        if (sessionLower.Contains("gnome") || sessionLower.Contains("ubuntu"))
            return new GnomeOverlayHelper();

        // Check for KDE-specific env vars
        if (Environment.GetEnvironmentVariable("KDE_FULL_SESSION") is not null)
            return new KdeOverlayHelper();

        // Check for GNOME-specific env vars
        if (Environment.GetEnvironmentVariable("GNOME_DESKTOP_SESSION_ID") is not null)
            return new GnomeOverlayHelper();

        // Default: X11 base with no compositor-specific active output detection.
        // KDE is more common for gaming on Linux, so default to that.
        return new KdeOverlayHelper();
    }
}
