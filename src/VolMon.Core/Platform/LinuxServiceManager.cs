using System.Diagnostics;

namespace VolMon.Core.Platform;

/// <summary>
/// Linux service manager using systemd user units and XDG autostart.
/// </summary>
public sealed class LinuxServiceManager : IServiceManager
{
    private const string ServiceName = "volmon";
    private static readonly string AutostartDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
    private static readonly string AutostartFile =
        Path.Combine(AutostartDir, "volmon-gui.desktop");

    public Task<bool> IsDaemonAutostartEnabledAsync()
    {
        var enabled = RunSystemctl("is-enabled", ServiceName) == 0;
        return Task.FromResult(enabled);
    }

    public Task<bool> IsGuiAutostartEnabledAsync()
    {
        // XDG autostart: file exists and doesn't contain Hidden=true
        if (!File.Exists(AutostartFile))
            return Task.FromResult(false);

        var content = File.ReadAllText(AutostartFile);
        var hidden = content.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(!hidden);
    }

    public Task SetDaemonAutostartAsync(bool enabled)
    {
        if (enabled)
            RunSystemctl("enable", ServiceName);
        else
            RunSystemctl("disable", ServiceName);

        return Task.CompletedTask;
    }

    public Task SetGuiAutostartAsync(bool enabled)
    {
        if (enabled)
        {
            // If the file exists but is hidden, remove Hidden=true
            if (File.Exists(AutostartFile))
            {
                var content = File.ReadAllText(AutostartFile);
                if (content.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Replace("Hidden=true", "Hidden=false");
                    File.WriteAllText(AutostartFile, content);
                }
            }
            // else: the register script creates this file; nothing to do if missing
        }
        else
        {
            if (File.Exists(AutostartFile))
            {
                var content = File.ReadAllText(AutostartFile);
                if (!content.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase))
                {
                    // Append Hidden=true to disable without deleting
                    content = content.TrimEnd() + "\nHidden=true\n";
                    File.WriteAllText(AutostartFile, content);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> RestartDaemonAsync()
    {
        // systemctl --user restart volmon
        // This will kill the current process and start a new one.
        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            ArgumentList = { "--user", "restart", ServiceName },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process.Start(psi);
        return Task.FromResult(true);
    }

    private static int RunSystemctl(string verb, string unit)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                ArgumentList = { "--user", verb, unit },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(TimeSpan.FromSeconds(10));
            return proc?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}
