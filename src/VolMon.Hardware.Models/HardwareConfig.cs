using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolMon.Hardware;

/// <summary>
/// Master configuration for the hardware daemon.
/// Stored at ~/.config/volmon/hardware.json.
/// Tracks all known devices and their enabled/disabled state.
/// </summary>
public sealed class HardwareConfig
{
    /// <summary>
    /// Known devices keyed by device ID (e.g. "beacn-mix-0041220700598").
    /// </summary>
    public Dictionary<string, DeviceEntry> Devices { get; set; } = [];

    /// <summary>
    /// Interval in seconds between USB device scans for hotplug detection.
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 5;

    // ── Serialization ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(appData, "volmon", "hardware.json");
    }

    public static string GetConfigDir()
    {
        return Path.GetDirectoryName(GetConfigPath())!;
    }

    public static async Task<HardwareConfig> LoadAsync(CancellationToken ct = default)
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
            return new HardwareConfig();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<HardwareConfig>(json, JsonOptions)
                ?? new HardwareConfig();
        }
        catch
        {
            return new HardwareConfig();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var path = GetConfigPath();
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }
}

/// <summary>
/// An entry in the hardware config representing a known device.
/// </summary>
public sealed class DeviceEntry
{
    /// <summary>Human-readable device name (e.g. "Beacn Mix").</summary>
    public string Name { get; set; } = "";

    /// <summary>Device driver type identifier (e.g. "beacn-mix").</summary>
    public string Driver { get; set; } = "";

    /// <summary>Device serial number.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Whether the device has a configurable display (supports layouts, brightness, etc.).</summary>
    public bool HasDisplay { get; set; }

    /// <summary>Whether the device is enabled. Disabled devices are not interacted with. New devices default to disabled.</summary>
    public bool Enabled { get; set; }
}
