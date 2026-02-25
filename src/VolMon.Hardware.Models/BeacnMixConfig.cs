using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolMon.Hardware.Beacn.Mix;

/// <summary>
/// Configuration for a Beacn Mix device. Stored per-device in the VolMon config folder
/// as ~/.config/volmon/beacn-mix-{serial}.json
/// </summary>
public sealed class BeacnMixConfig
{
    /// <summary>Display brightness when active (0-100). Default: 40.</summary>
    public int DisplayBrightness { get; set; } = 40;

    /// <summary>Display brightness when dimmed (0-100). Default: 1.</summary>
    public int DimBrightness { get; set; } = 1;

    /// <summary>Button LED brightness (0-10). Default: 8.</summary>
    public int ButtonBrightness { get; set; } = 8;

    /// <summary>Seconds of inactivity before dimming the display. Default: 30.</summary>
    public int DimTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds of inactivity before turning off the display entirely. Default: 60.</summary>
    public int OffTimeoutSeconds { get; set; } = 60;

    /// <summary>Volume change per unit of dial rotation (percentage points). Default: 1.</summary>
    public int VolumeStepPerDelta { get; set; } = 1;

    /// <summary>
    /// Display layout name. Matches a bundled preset or custom layout filename (without .json).
    /// Checked against bundled Layouts/ folder first, then the config folder (~/.config/volmon/).
    /// </summary>
    public string Layout { get; set; } = "VolMon_Layout_BeacnMix_default-vertical";

    // ── Serialization ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>
    /// Get the config file path for a device with the given serial number.
    /// </summary>
    public static string GetConfigPath(string serial)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        var sanitized = string.IsNullOrWhiteSpace(serial) ? "unknown" : serial.Trim();
        return Path.Combine(appData, "volmon", $"beacn-mix-{sanitized}.json");
    }

    /// <summary>
    /// Load config from disk, or return defaults if the file doesn't exist.
    /// </summary>
    public static async Task<BeacnMixConfig> LoadAsync(string serial)
    {
        var path = GetConfigPath(serial);
        if (!File.Exists(path))
        {
            var config = new BeacnMixConfig();
            await config.SaveAsync(serial);
            return config;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<BeacnMixConfig>(json, JsonOptions) ?? new BeacnMixConfig();
        }
        catch
        {
            return new BeacnMixConfig();
        }
    }

    /// <summary>
    /// Save config to disk.
    /// </summary>
    public async Task SaveAsync(string serial)
    {
        var path = GetConfigPath(serial);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }
}
