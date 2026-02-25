using VolMon.Hardware;
using VolMon.Hardware.Beacn.Mix;

namespace VolMon.HardwareGUI.Services;

/// <summary>
/// Reads and writes the hardware daemon config files directly.
/// Uses the shared config models from VolMon.Hardware.Models.
/// </summary>
internal static class HardwareConfigService
{
    // ── Paths ───────────────────────────────────────────────────────

    public static string GetConfigDir() => HardwareConfig.GetConfigDir();

    public static string GetHardwareConfigPath() => HardwareConfig.GetConfigPath();

    public static string GetDeviceConfigPath(string serial) => BeacnMixConfig.GetConfigPath(serial);

    // ── Hardware config (hardware.json) ─────────────────────────────

    public static Task<HardwareConfig> LoadHardwareConfigAsync(CancellationToken ct = default) =>
        HardwareConfig.LoadAsync(ct);

    public static Task SaveHardwareConfigAsync(HardwareConfig config, CancellationToken ct = default) =>
        config.SaveAsync(ct);

    // ── Device config (beacn-mix-{serial}.json) ─────────────────────

    public static Task<BeacnMixConfig> LoadDeviceConfigAsync(string serial, CancellationToken ct = default) =>
        BeacnMixConfig.LoadAsync(serial);

    public static Task SaveDeviceConfigAsync(string serial, BeacnMixConfig config, CancellationToken ct = default) =>
        config.SaveAsync(serial);

    // ── Bundled layouts ─────────────────────────────────────────────

    /// <summary>
    /// List available bundled layout preset names from the Hardware daemon's
    /// Layouts directory. Checks multiple locations (installed, dev build output,
    /// dev source) and returns layout names without the .json extension.
    /// </summary>
    public static string[] ListBundledLayouts()
    {
        var baseDir = AppContext.BaseDirectory;

        var dirs = new[]
        {
            // Installed side-by-side or published together
            Path.Combine(baseDir, "Layouts"),
            // Sibling Hardware project build output (dev)
            Path.Combine(baseDir, "..", "VolMon.Hardware", "net10.0", "Layouts"),
            // Sibling Hardware project source layouts (dev, going up from bin/Debug/net10.0)
            Path.Combine(baseDir, "..", "..", "..", "..", "VolMon.Hardware", "Beacn", "Mix", "Layouts"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir, "VolMon_Layout_*.json");
            if (files.Length == 0) continue;

            return files
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n)
                .ToArray();
        }

        // Fallback: return known defaults
        return
        [
            "VolMon_Layout_BeacnMix_default-vertical",
            "VolMon_Layout_BeacnMix_horizontal-bars",
            "VolMon_Layout_BeacnMix_compact-grid",
            "VolMon_Layout_BeacnMix_horseshoe-gauges"
        ];
    }
}
