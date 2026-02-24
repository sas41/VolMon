using System.Diagnostics;

namespace VolMon.Core.Platform;

/// <summary>
/// macOS service manager using launchd user agents.
/// </summary>
public sealed class MacOsServiceManager : IServiceManager
{
    private const string DaemonLabel = "com.volmon.daemon";
    private const string GuiLabel = "com.volmon.gui";

    private static readonly string LaunchAgentsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");

    private static readonly string DaemonPlist =
        Path.Combine(LaunchAgentsDir, $"{DaemonLabel}.plist");

    private static readonly string GuiPlist =
        Path.Combine(LaunchAgentsDir, $"{GuiLabel}.plist");

    public Task<bool> IsDaemonAutostartEnabledAsync()
    {
        var enabled = File.Exists(DaemonPlist) && IsAgentLoaded(DaemonLabel);
        return Task.FromResult(enabled);
    }

    public Task<bool> IsGuiAutostartEnabledAsync()
    {
        var enabled = File.Exists(GuiPlist) && IsAgentLoaded(GuiLabel);
        return Task.FromResult(enabled);
    }

    public Task SetDaemonAutostartAsync(bool enabled)
    {
        SetAgentEnabled(DaemonLabel, DaemonPlist, enabled);
        return Task.CompletedTask;
    }

    public Task SetGuiAutostartAsync(bool enabled)
    {
        SetAgentEnabled(GuiLabel, GuiPlist, enabled);
        return Task.CompletedTask;
    }

    public Task<bool> RestartDaemonAsync()
    {
        var uid = GetUid();
        // kickstart -k forces a restart (kills existing + starts new)
        var psi = new ProcessStartInfo
        {
            FileName = "launchctl",
            ArgumentList = { "kickstart", "-k", $"gui/{uid}/{DaemonLabel}" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process.Start(psi);
        return Task.FromResult(true);
    }

    private static bool IsAgentLoaded(string label)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "launchctl",
                ArgumentList = { "list", label },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(TimeSpan.FromSeconds(5));
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAgentEnabled(string label, string plistPath, bool enabled)
    {
        if (!File.Exists(plistPath)) return;

        var uid = GetUid();
        try
        {
            if (enabled)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "launchctl",
                    ArgumentList = { "bootstrap", $"gui/{uid}", plistPath },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(TimeSpan.FromSeconds(5));

                // Fallback for older macOS
                if (proc?.ExitCode != 0)
                {
                    var fallback = new ProcessStartInfo
                    {
                        FileName = "launchctl",
                        ArgumentList = { "load", "-w", plistPath },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc2 = Process.Start(fallback);
                    proc2?.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "launchctl",
                    ArgumentList = { "bootout", $"gui/{uid}/{label}" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(TimeSpan.FromSeconds(5));

                // Fallback
                if (proc?.ExitCode != 0)
                {
                    var fallback = new ProcessStartInfo
                    {
                        FileName = "launchctl",
                        ArgumentList = { "unload", "-w", plistPath },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc2 = Process.Start(fallback);
                    proc2?.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string GetUid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "id",
                ArgumentList = { "-u" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit(TimeSpan.FromSeconds(3));
            return output ?? "501";
        }
        catch
        {
            return "501"; // fallback default macOS UID
        }
    }
}
