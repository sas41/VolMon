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
    /// <summary>
    /// Returns the bundled layouts directory: Hardware/Beacn/Mix/Layouts/ next to
    /// the running executable. Mirrors the path used by the hardware daemon.
    /// </summary>
    private static string GetBundledLayoutsDir() =>
        Path.Combine(AppContext.BaseDirectory, "Hardware", "Beacn", "Mix", "Layouts");

    /// <summary>
    /// Returns the user layouts directory inside the VolMon config folder.
    /// Users can place custom layout JSON files here.
    /// </summary>
    private static string GetUserLayoutsDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(appData, "volmon", "Hardware", "Beacn", "Mix", "Layouts");
    }

    public static string[] ListBundledLayouts()
    {
        var names = new List<string>();

        // 1. Bundled layouts next to the exe (installed or published)
        CollectLayouts(GetBundledLayoutsDir(), names);

        // 2. User layouts from config folder
        CollectLayouts(GetUserLayoutsDir(), names);

        if (names.Count > 0)
            return [.. names.OrderBy(n => n)];

        var baseDir = AppContext.BaseDirectory;

        // 3. Dev fallbacks: sibling build output or source tree
        var devDirs = new[]
        {
            Path.Combine(baseDir, "..", "VolMon.Hardware", "net10.0", "Hardware", "Beacn", "Mix", "Layouts"),
            Path.Combine(baseDir, "..", "..", "..", "..", "VolMon.Hardware", "Beacn", "Mix", "Layouts"),
        };

        foreach (var dir in devDirs)
        {
            CollectLayouts(dir, names);
            if (names.Count > 0)
                return [.. names.OrderBy(n => n)];
        }

        // Last resort: known defaults
        return
        [
            "VolMon_Layout_BeacnMix_default-vertical",
            "VolMon_Layout_BeacnMix_horizontal-bars",
            "VolMon_Layout_BeacnMix_compact-grid",
            "VolMon_Layout_BeacnMix_horseshoe-gauges"
        ];
    }

    private static void CollectLayouts(string dir, List<string> names)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "VolMon_Layout_*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!names.Contains(name))
                names.Add(name);
        }
    }
}
