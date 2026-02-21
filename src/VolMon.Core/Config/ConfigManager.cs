using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolMon.Core.Config;

/// <summary>
/// Loads, saves, and watches the VolMon config file.
/// </summary>
public sealed class ConfigManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _configPath;
    private FileSystemWatcher? _watcher;
    private VolMonConfig _config = new();

    /// <summary>
    /// Raised when the config file changes on disk.
    /// </summary>
    public event EventHandler<VolMonConfig>? ConfigChanged;

    public VolMonConfig Config => _config;
    public string ConfigPath => _configPath;

    public ConfigManager() : this(GetDefaultConfigPath()) { }

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Returns the platform-appropriate default config file path.
    /// </summary>
    public static string GetDefaultConfigPath()
    {
        // On Linux this returns ~/.config, on Windows %APPDATA%, on macOS ~/Library/Application Support
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Fallback for Linux if the above returns empty
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(appData, "volmon", "config.json");
    }

    /// <summary>
    /// Loads the config from disk. Creates a default config if the file doesn't exist.
    /// </summary>
    public async Task<VolMonConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configPath))
        {
            _config = new VolMonConfig();
            await SaveAsync(ct);
            return _config;
        }

        var json = await File.ReadAllTextAsync(_configPath, ct);
        _config = JsonSerializer.Deserialize<VolMonConfig>(json, JsonOptions) ?? new VolMonConfig();
        return _config;
    }

    /// <summary>
    /// Saves the current config to disk.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_config, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json, ct);
    }

    /// <summary>
    /// Starts watching the config file for external changes.
    /// </summary>
    public void StartWatching()
    {
        var dir = Path.GetDirectoryName(_configPath);
        var file = Path.GetFileName(_configPath);

        if (dir is null || file is null)
            return;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += async (_, _) =>
        {
            try
            {
                // Small delay to let the file finish writing
                await Task.Delay(100);
                await LoadAsync();
                ConfigChanged?.Invoke(this, _config);
            }
            catch
            {
                // Config file might be mid-write; ignore and wait for next event
            }
        };
    }

    /// <summary>
    /// Stops watching the config file.
    /// </summary>
    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        StopWatching();
    }
}
