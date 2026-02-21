using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// GNOME Shell / Mutter overlay helper. Uses GNOME Shell's D-Bus Eval interface
/// to query which output the cursor is currently on. This works even when
/// no XWayland window has focus.
/// </summary>
public sealed partial class GnomeOverlayHelper : X11OverlayBase
{
    /// <inheritdoc />
    public override string? GetActiveOutputName()
    {
        try
        {
            // GNOME Shell exposes a JS eval interface via D-Bus.
            // global.display.get_current_monitor() returns the monitor index,
            // and global.display.get_monitor_geometry(i) has the details.
            // But for the output name we need:
            //   Meta.MonitorManager.get().get_monitor_info(i).connector
            // Simpler approach: get the monitor index under the pointer, then
            // get the connector name for that index.
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gdbus",
                    Arguments = """call --session --dest org.gnome.Shell --object-path /org/gnome/Shell --method org.gnome.Shell.Eval "let m = global.display.get_current_monitor(); let info = global.display.get_monitor_geometry(m); global.display.get_monitor_connector(m);" """,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(500);

            // Output format: (true, 'DP-1') or (true, '"DP-1"')
            // Extract the connector name from the response.
            if (string.IsNullOrEmpty(output)) return null;

            var match = ConnectorRegex().Match(output);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"'""?([^'""]+)""?'")]
    private static partial Regex ConnectorRegex();
}
