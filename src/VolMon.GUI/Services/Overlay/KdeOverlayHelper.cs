using System.Diagnostics;

namespace VolMon.GUI.Services.Overlay;

/// <summary>
/// KDE Plasma / KWin overlay helper. Uses KWin's D-Bus interface to query
/// which output (monitor) the cursor is currently on. This works even when
/// no XWayland window has focus (e.g. clicking empty desktop space).
/// </summary>
public sealed class KdeOverlayHelper : X11OverlayBase
{
    /// <inheritdoc />
    public override string? GetActiveOutputName()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "qdbus6",
                    Arguments = "org.kde.KWin /KWin org.kde.KWin.activeOutputName",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(500);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
