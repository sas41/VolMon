using System.Diagnostics;

namespace VolMon.Core.Platform;

/// <summary>
/// Windows service manager using Task Scheduler and shell:startup shortcuts.
/// </summary>
public sealed class WindowsServiceManager : IServiceManager
{
    private const string TaskName = "VolMon Daemon";
    private static readonly string StartupDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static readonly string ShortcutPath =
        Path.Combine(StartupDir, "VolMon GUI.lnk");

    public Task<bool> IsDaemonAutostartEnabledAsync()
    {
        // Check if the scheduled task exists and is enabled
        var (exitCode, output) = RunSchtasks("/Query", "/TN", TaskName, "/FO", "CSV", "/NH");
        if (exitCode != 0)
            return Task.FromResult(false);

        // CSV output contains "Ready" or "Disabled" in the status column
        var enabled = output.Contains("Ready", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(enabled);
    }

    public Task<bool> IsGuiAutostartEnabledAsync()
    {
        return Task.FromResult(File.Exists(ShortcutPath));
    }

    public Task SetDaemonAutostartAsync(bool enabled)
    {
        if (enabled)
            RunSchtasks("/Change", "/TN", TaskName, "/ENABLE");
        else
            RunSchtasks("/Change", "/TN", TaskName, "/DISABLE");

        return Task.CompletedTask;
    }

    public Task SetGuiAutostartAsync(bool enabled)
    {
        if (!enabled && File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
        // Re-enabling requires recreating the shortcut, which the register
        // script handles. We can't easily create a .lnk from pure C#
        // without COM interop. For now, disabling removes the shortcut and
        // re-enabling is a no-op (user should re-run register.ps1).
        // A future improvement could use IWshRuntimeLibrary on Windows.

        return Task.CompletedTask;
    }

    public Task<bool> RestartDaemonAsync()
    {
        // Stop then start the task. schtasks /Run starts it even if disabled,
        // so we use /End first, then /Run.
        RunSchtasks("/End", "/TN", TaskName);
        RunSchtasks("/Run", "/TN", TaskName);
        return Task.FromResult(true);
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(TimeSpan.FromSeconds(10));
            return (proc?.ExitCode ?? -1, output);
        }
        catch
        {
            return (-1, "");
        }
    }
}
