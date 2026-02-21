using VolMon.Core.Audio;
using VolMon.Core.Config;

namespace VolMon.Daemon;

/// <summary>
/// Watches for audio stream and device events and applies group rules.
/// New programs go to the default group. New hardware is left unassigned.
/// </summary>
public sealed class StreamWatcher : IDisposable
{
    private readonly IAudioBackend _backend;
    private readonly ConfigManager _configManager;
    private readonly ILogger<StreamWatcher> _logger;

    /// <summary>Cache of currently active streams, keyed by stream ID.</summary>
    private readonly Dictionary<string, AudioStream> _activeStreams = [];

    /// <summary>Cache of known devices, keyed by device name.</summary>
    private readonly Dictionary<string, AudioDevice> _knownDevices = [];

    public IReadOnlyDictionary<string, AudioStream> ActiveStreams => _activeStreams;
    public IReadOnlyDictionary<string, AudioDevice> KnownDevices => _knownDevices;

    public StreamWatcher(IAudioBackend backend, ConfigManager configManager, ILogger<StreamWatcher> logger)
    {
        _backend = backend;
        _configManager = configManager;
        _logger = logger;

        _backend.StreamCreated += OnStreamCreated;
        _backend.StreamRemoved += OnStreamRemoved;
        _backend.StreamChanged += OnStreamChanged;
        _backend.DeviceChanged += OnDeviceChanged;
    }

    /// <summary>
    /// Performs an initial scan of all active streams and devices, then applies group rules.
    /// </summary>
    public async Task InitialScanAsync(CancellationToken ct = default)
    {
        // Scan streams
        var streams = await _backend.GetStreamsAsync(ct);
        foreach (var stream in streams)
        {
            _activeStreams[stream.Id] = stream;
            AssignStreamToGroup(stream);
            await ApplyStreamSettingsAsync(stream, ct);
        }
        _logger.LogInformation("Initial scan found {Count} active streams", streams.Count);

        // Scan devices
        var devices = await _backend.GetDevicesAsync(ct);
        foreach (var device in devices)
        {
            _knownDevices[device.Name] = device;
            AssignDeviceToGroup(device);
            await ApplyDeviceSettingsAsync(device, ct);
        }
        _logger.LogInformation("Initial scan found {Count} audio devices", devices.Count);
    }

    /// <summary>
    /// Applies volume/mute settings to all streams and devices in the specified group.
    /// Ignored groups are skipped — their members' volumes are never changed.
    /// </summary>
    public async Task ApplyGroupSettingsAsync(AudioGroup group, CancellationToken ct = default)
    {
        if (group.IsIgnored)
        {
            _logger.LogDebug("Skipping volume for ignored group {Group}", group.Name);
            return;
        }

        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        var streams = _activeStreams.Values.Where(s => s.AssignedGroup == group.Id).ToList();
        foreach (var stream in streams)
        {
            try
            {
                await _backend.SetStreamVolumeAsync(stream.Id, group.Volume, ct);
                await _backend.SetStreamMuteAsync(stream.Id, group.Muted, ct);
                stream.Volume = group.Volume;
                stream.Muted = group.Muted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply settings to stream {StreamId}", stream.Id);
            }
        }

        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        var devices = _knownDevices.Values.Where(d => d.AssignedGroup == group.Id).ToList();
        foreach (var device in devices)
        {
            try
            {
                await _backend.SetDeviceVolumeAsync(device.Name, group.Volume, ct);
                await _backend.SetDeviceMuteAsync(device.Name, group.Muted, ct);
                device.Volume = group.Volume;
                device.Muted = group.Muted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply settings to device {DeviceName}", device.Name);
            }
        }
    }

    /// <summary>
    /// Re-evaluates all active streams and devices against current group rules.
    /// </summary>
    public async Task ReassignAllAsync(CancellationToken ct = default)
    {
        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        foreach (var stream in _activeStreams.Values.ToList())
        {
            stream.AssignedGroup = null;
            AssignStreamToGroup(stream);
            await ApplyStreamSettingsAsync(stream, ct);
        }

        foreach (var device in _knownDevices.Values.ToList())
        {
            device.AssignedGroup = null;
            AssignDeviceToGroup(device);
            await ApplyDeviceSettingsAsync(device, ct);
        }
    }

    // ── Event handlers ───────────────────────────────────────────────

    private async void OnStreamCreated(object? sender, AudioStreamEventArgs e)
    {
        try
        {
            var streams = await _backend.GetStreamsAsync();
            var stream = streams.FirstOrDefault(s => s.Id == e.StreamId);
            if (stream is null) return;
            AssignStreamToGroup(stream);

            _activeStreams[stream.Id] = stream;
            _logger.LogInformation("New stream: {Stream}", stream);

            await ApplyStreamSettingsAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling new stream {StreamId}", e.StreamId);
        }
    }

    private void OnStreamRemoved(object? sender, AudioStreamEventArgs e)
    {
        if (_activeStreams.Remove(e.StreamId))
        {
            _logger.LogInformation("Stream removed: {StreamId}", e.StreamId);
        }
    }

    private async void OnStreamChanged(object? sender, AudioStreamEventArgs e)
    {
        try
        {
            var streams = await _backend.GetStreamsAsync();
            var stream = streams.FirstOrDefault(s => s.Id == e.StreamId);
            if (stream is null) return;

            // Preserve group assignment
            if (_activeStreams.TryGetValue(e.StreamId, out var existing))
                stream.AssignedGroup = existing.AssignedGroup;

            _activeStreams[e.StreamId] = stream;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling stream change {StreamId}", e.StreamId);
        }
    }

    private async void OnDeviceChanged(object? sender, AudioDeviceEventArgs e)
    {
        try
        {
            // Refresh all devices (pactl subscribe gives us numeric IDs, not names)
            var devices = await _backend.GetDevicesAsync();
            _knownDevices.Clear();
            foreach (var device in devices)
            {
                _knownDevices[device.Name] = device;
                // Only assign device to group if it's already in config — new hardware stays unassigned
                AssignDeviceToGroup(device);
            }

            _logger.LogInformation("Device change detected, refreshed {Count} devices", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling device change");
        }
    }

    // ── Assignment logic ─────────────────────────────────────────────

    /// <summary>
    /// Assigns a stream to the first group whose Programs list contains its binary name.
    /// If no group matches, assigns to the default group.
    /// </summary>
    private void AssignStreamToGroup(AudioStream stream)
    {
        var config = _configManager.Config;

        // Check explicit program lists first
        foreach (var group in config.Groups)
        {
            if (group.ContainsProgram(stream))
            {
                stream.AssignedGroup = group.Id;
                _logger.LogInformation("Stream {Binary} assigned to group {Group} ({Id})",
                    stream.BinaryName, group.Name, group.Id);
                return;
            }
        }

        // Not in any group's program list — assign to default group
        var defaultGroup = config.Groups.FirstOrDefault(g => g.IsDefault);
        if (defaultGroup is not null)
        {
            stream.AssignedGroup = defaultGroup.Id;
            _logger.LogInformation("Stream {Binary} assigned to default group {Group} ({Id})",
                stream.BinaryName, defaultGroup.Name, defaultGroup.Id);
        }
    }

    /// <summary>
    /// Assigns a device to the first group whose Devices list contains its name.
    /// New/unknown hardware is NOT auto-assigned anywhere.
    /// </summary>
    private void AssignDeviceToGroup(AudioDevice device)
    {
        var config = _configManager.Config;

        foreach (var group in config.Groups)
        {
            if (group.ContainsDevice(device.Name))
            {
                device.AssignedGroup = group.Id;
                _logger.LogInformation("Device {Name} assigned to group {Group} ({Id})",
                    device.Name, group.Name, group.Id);
                return;
            }
        }

        // Not in any group — leave unassigned
    }

    private async Task ApplyStreamSettingsAsync(AudioStream stream, CancellationToken ct = default)
    {
        if (stream.AssignedGroup is null) return;

        var group = _configManager.Config.Groups.FirstOrDefault(
            g => g.Id == stream.AssignedGroup);
        if (group is null) return;

        // Ignored group — never touch volume
        if (group.IsIgnored) return;

        try
        {
            await _backend.SetStreamVolumeAsync(stream.Id, group.Volume, ct);
            await _backend.SetStreamMuteAsync(stream.Id, group.Muted, ct);
            stream.Volume = group.Volume;
            stream.Muted = group.Muted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply settings to stream {StreamId}", stream.Id);
        }
    }

    private async Task ApplyDeviceSettingsAsync(AudioDevice device, CancellationToken ct = default)
    {
        if (device.AssignedGroup is null) return;

        var group = _configManager.Config.Groups.FirstOrDefault(
            g => g.Id == device.AssignedGroup);
        if (group is null) return;

        try
        {
            await _backend.SetDeviceVolumeAsync(device.Name, group.Volume, ct);
            await _backend.SetDeviceMuteAsync(device.Name, group.Muted, ct);
            device.Volume = group.Volume;
            device.Muted = group.Muted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply settings to device {DeviceName}", device.Name);
        }
    }

    public void Dispose()
    {
        _backend.StreamCreated -= OnStreamCreated;
        _backend.StreamRemoved -= OnStreamRemoved;
        _backend.StreamChanged -= OnStreamChanged;
        _backend.DeviceChanged -= OnDeviceChanged;
    }
}
