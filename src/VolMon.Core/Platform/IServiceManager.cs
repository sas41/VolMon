namespace VolMon.Core.Platform;

/// <summary>
/// Platform-specific operations for managing the VolMon daemon service
/// and GUI autostart registration.
/// </summary>
public interface IServiceManager
{
    /// <summary>Whether the daemon is registered to start automatically on login.</summary>
    Task<bool> IsDaemonAutostartEnabledAsync();

    /// <summary>Whether the GUI is registered to start automatically on login.</summary>
    Task<bool> IsGuiAutostartEnabledAsync();

    /// <summary>Enable or disable daemon autostart.</summary>
    Task SetDaemonAutostartAsync(bool enabled);

    /// <summary>Enable or disable GUI autostart.</summary>
    Task SetGuiAutostartAsync(bool enabled);

    /// <summary>
    /// Restart the daemon process. The implementation should start a new
    /// daemon instance (via the OS service manager) and then the current
    /// process should exit. Returns false if the platform doesn't support
    /// managed restarts.
    /// </summary>
    Task<bool> RestartDaemonAsync();
}
