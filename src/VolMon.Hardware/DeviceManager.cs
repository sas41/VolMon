using Microsoft.Extensions.Logging;
using VolMon.Core.Audio;
using VolMon.Core.Ipc;
using AudioProcessInfo = VolMon.Core.Ipc.AudioProcessInfo;
using AudioDeviceInfo = VolMon.Core.Ipc.AudioDeviceInfo;

namespace VolMon.Hardware;

/// <summary>
/// Manages all hardware device sessions. Periodically scans for USB devices,
/// starts/stops sessions based on hardware.json config, watches for config
/// changes, and broadcasts daemon state updates to all active sessions.
/// </summary>
internal sealed class DeviceManager : IAsyncDisposable
{
    private readonly IReadOnlyList<IDeviceDriver> _drivers;
    private readonly IpcDuplexClient _ipc;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DeviceManager> _logger;
    private readonly Dictionary<string, DeviceSession> _sessions = [];
    private readonly Lock _sessionsLock = new();

    private HardwareConfig _config = new();
    private FileSystemWatcher? _configWatcher;
    private CancellationTokenSource? _cts;
    private Task? _scanTask;

    public DeviceManager(
        IReadOnlyList<IDeviceDriver> drivers,
        IpcDuplexClient ipc,
        ILoggerFactory loggerFactory)
    {
        _drivers = drivers;
        _ipc = ipc;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeviceManager>();
    }

    /// <summary>
    /// Load config, start scanning for devices, and watch for config changes.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _config = await HardwareConfig.LoadAsync(ct);
        StartConfigWatcher();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scanTask = Task.Run(() => ScanLoop(_cts.Token), _cts.Token);

        // Do an immediate scan
        await ScanAndReconcileAsync();
    }

    /// <summary>
    /// Stop all sessions and cleanup.
    /// </summary>
    public async Task StopAsync()
    {
        StopConfigWatcher();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_scanTask is not null)
            {
                try { await _scanTask; }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }

        // Stop all sessions
        List<DeviceSession> sessions;
        lock (_sessionsLock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping session for {Device}", session.DeviceId);
            }
        }
    }

    /// <summary>
    /// Broadcast a daemon state-changed event to all active sessions.
    /// </summary>
    public void BroadcastStateChanged(
        List<AudioGroup> groups,
        List<AudioProcessInfo>? processes = null,
        List<AudioDeviceInfo>? devices = null)
    {
        List<DeviceSession> sessions;
        lock (_sessionsLock)
        {
            sessions = [.. _sessions.Values];
        }

        foreach (var session in sessions)
        {
            session.OnDaemonStateChanged(groups, processes, devices);
        }
    }

    // ── Periodic scan ───────────────────────────────────────────────

    private async Task ScanLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.ScanIntervalSeconds), ct);
                await ScanAndReconcileAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during device scan");
            }
        }
    }

    /// <summary>
    /// Scan all drivers for connected devices, reconcile with config and running sessions.
    /// </summary>
    private async Task ScanAndReconcileAsync()
    {
        // Collect IDs of devices with active (running) sessions so drivers can skip them
        HashSet<string> activeIds;
        lock (_sessionsLock)
        {
            activeIds = _sessions
                .Where(kv => kv.Value.State == DeviceSessionState.Running)
                .Select(kv => kv.Key)
                .ToHashSet();
        }

        // Discover all connected devices (drivers will skip already-open devices)
        var detected = new Dictionary<string, (DetectedDevice Device, IDeviceDriver Driver)>();
        foreach (var driver in _drivers)
        {
            try
            {
                var devices = driver.Scan(activeIds);
                foreach (var d in devices)
                    detected[d.DeviceId] = (d, driver);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning driver {Driver}", driver.DriverType);
            }
        }

        var configDirty = false;

        // Ensure every detected device has a config entry, and update existing entries
        foreach (var (deviceId, (device, _)) in detected)
        {
            if (_config.Devices.TryGetValue(deviceId, out var existing))
            {
                // Update fields the driver is authoritative for
                if (existing.HasDisplay != device.HasDisplay ||
                    existing.Name != device.DeviceName ||
                    existing.Driver != device.DriverType)
                {
                    existing.HasDisplay = device.HasDisplay;
                    existing.Name = device.DeviceName;
                    existing.Driver = device.DriverType;
                    configDirty = true;
                }
            }
            else
            {
                _config.Devices[deviceId] = new DeviceEntry
                {
                    Name = device.DeviceName,
                    Driver = device.DriverType,
                    Serial = device.Serial,
                    HasDisplay = device.HasDisplay,
                    Enabled = false
                };
                configDirty = true;
                _logger.LogInformation("New device detected: {Name} ({Id})", device.DeviceName, deviceId);
            }
        }

        if (configDirty)
        {
            try { await _config.SaveAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save hardware config after detecting new devices");
            }
        }

        // Start sessions for enabled + connected devices that aren't already running
        foreach (var (deviceId, (device, driver)) in detected)
        {
            if (!_config.Devices.TryGetValue(deviceId, out var entry) || !entry.Enabled)
                continue;

            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(deviceId, out var existing))
                {
                    // If the session faulted, tear it down so we can retry
                    if (existing.State == DeviceSessionState.Faulted)
                    {
                        _logger.LogWarning("[{Device}] Faulted session detected, restarting", deviceId);
                        _ = Task.Run(async () =>
                        {
                            try { await existing.DisposeAsync(); }
                            catch { /* best effort */ }
                        });
                        _sessions.Remove(deviceId);
                    }
                    else
                    {
                        continue; // Already running
                    }
                }
            }

            StartSession(device, driver);
        }

        // Stop sessions for devices that are no longer connected or have been disabled
        List<string> toRemove = [];
        lock (_sessionsLock)
        {
            foreach (var (deviceId, session) in _sessions)
            {
                var stillConnected = detected.ContainsKey(deviceId);
                var stillEnabled = _config.Devices.TryGetValue(deviceId, out var entry) && entry.Enabled;

                if (!stillConnected || !stillEnabled)
                    toRemove.Add(deviceId);
            }
        }

        foreach (var deviceId in toRemove)
        {
            DeviceSession? session;
            lock (_sessionsLock)
            {
                _sessions.Remove(deviceId, out session);
            }

            if (session is not null)
            {
                _logger.LogInformation("[{Device}] Stopping session (disconnected or disabled)", deviceId);
                try { await session.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping session for {Device}", deviceId);
                }
            }
        }
    }

    private void StartSession(DetectedDevice device, IDeviceDriver driver)
    {
        try
        {
            var controller = driver.CreateController(device, _loggerFactory);
            var sessionLogger = _loggerFactory.CreateLogger<DeviceSession>();
            var session = new DeviceSession(controller, _ipc, sessionLogger);

            lock (_sessionsLock)
            {
                _sessions[device.DeviceId] = session;
            }

            session.Start();
            _logger.LogInformation("[{Device}] Session started for {Name}", device.DeviceId, device.DeviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session for {Device}", device.DeviceId);
        }
    }

    // ── Config file watcher ─────────────────────────────────────────

    private void StartConfigWatcher()
    {
        var configPath = HardwareConfig.GetConfigPath();
        var dir = Path.GetDirectoryName(configPath);
        var file = Path.GetFileName(configPath);

        if (dir is null || file is null) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _configWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _configWatcher.Changed += OnConfigFileChanged;
    }

    private void StopConfigWatcher()
    {
        if (_configWatcher is not null)
        {
            _configWatcher.Changed -= OnConfigFileChanged;
            _configWatcher.Dispose();
            _configWatcher = null;
        }
    }

    private async void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(150); // Debounce
            _config = await HardwareConfig.LoadAsync();
            _logger.LogInformation("Hardware config reloaded");

            // Immediately reconcile — this will start/stop sessions as needed
            await ScanAndReconcileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload hardware config");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
