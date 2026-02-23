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

    /// <summary>
    /// Last snapshot of running configured process names, used to detect
    /// process start/quit without audio streams.
    /// </summary>
    private HashSet<string> _lastConfiguredRunning = [];
    private CancellationTokenSource? _processPollCts;

    public IReadOnlyDictionary<string, AudioStream> ActiveStreams => _activeStreams;
    public IReadOnlyDictionary<string, AudioDevice> KnownDevices => _knownDevices;

    /// <summary>
    /// Raised whenever the internal state changes (streams added/removed/changed,
    /// devices changed). The daemon subscribes to this to broadcast updates to
    /// connected IPC clients. Debounced to avoid event storms when multiple
    /// streams change in rapid succession (e.g. applying volume to a group).
    /// </summary>
    public event EventHandler? StateChanged;

    private CancellationTokenSource? _stateChangedDebounce;
    private const int StateChangedDebounceMs = 100;

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

        // Seed the initial set and start the poll loop.
        _lastConfiguredRunning = GetRunningConfiguredProcesses();
        _processPollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PollConfiguredProcessesAsync(_processPollCts.Token);
    }

    /// <summary>
    /// Polls running processes once per second and fires <see cref="StateChanged"/>
    /// when a configured process appears or disappears, even if it never opens
    /// an audio stream.
    /// </summary>
    private async Task PollConfiguredProcessesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);

                var current = GetRunningConfiguredProcesses();
                if (!current.SetEquals(_lastConfiguredRunning))
                {
                    _lastConfiguredRunning = current;
                    RaiseStateChanged();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Returns the set of configured program names that are currently running.
    /// On Linux reads /proc; on other platforms uses Process.GetProcesses().
    /// </summary>
    private HashSet<string> GetRunningConfiguredProcesses()
    {
        var configured = _configManager.Config.Groups
            .SelectMany(g => g.Programs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configured.Count == 0)
            return [];

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    if (configured.Contains(comm))
                        running.Add(comm);
                }
                catch { /* process exited */ }
            }
        }
        else
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (configured.Contains(proc.ProcessName))
                        running.Add(proc.ProcessName);
                }
                catch { /* access denied */ }
            }
        }

        return running;
    }

    /// <summary>
    /// Applies volume/mute settings to all streams and devices in the specified group.
    /// </summary>
    public async Task ApplyGroupSettingsAsync(AudioGroup group, CancellationToken ct = default)
    {

        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        var streams = _activeStreams.Values.Where(s => s.AssignedGroup == group.Id).ToArray();
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
                _logger.LogDebug(ex, "Failed to apply settings to stream {StreamId} (transient; stream may still exist)", stream.Id);
            }
        }

        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        var devices = _knownDevices.Values.Where(d => d.AssignedGroup == group.Id).ToArray();
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
        foreach (var stream in _activeStreams.Values.ToArray())
        {
            stream.AssignedGroup = null;
            AssignStreamToGroup(stream);
            await ApplyStreamSettingsAsync(stream, ct);
        }

        foreach (var device in _knownDevices.Values.ToArray())
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
            RaiseStateChanged();
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
            RaiseStateChanged();
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
            RaiseStateChanged();
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
            RaiseStateChanged();
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

        // Ignored programs are never assigned to any group.
        if (config.IgnoredPrograms.Contains(stream.BinaryName, StringComparer.OrdinalIgnoreCase))
        {
            stream.AssignedGroup = null;
            return;
        }

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



        try
        {
            await _backend.SetStreamVolumeAsync(stream.Id, group.Volume, ct);
            await _backend.SetStreamMuteAsync(stream.Id, group.Muted, ct);
            stream.Volume = group.Volume;
            stream.Muted = group.Muted;
        }
        catch (Exception ex)
        {
            // Don't remove from _activeStreams here — a failed pactl call can be
            // a transient PulseAudio/PipeWire hiccup, not proof the stream is gone.
            // The authoritative removal signal is the 'remove' event from pactl subscribe.
            _logger.LogDebug(ex, "Failed to apply settings to stream {StreamId} (transient; stream may still exist)", stream.Id);
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

    /// <summary>
    /// Debounces rapid StateChanged notifications. Multiple calls within
    /// <see cref="StateChangedDebounceMs"/> are coalesced into one.
    /// </summary>
    private void RaiseStateChanged()
    {
        var old = _stateChangedDebounce;
        old?.Cancel();

        var cts = new CancellationTokenSource();
        _stateChangedDebounce = cts;
        var ct = cts.Token;

        old?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StateChangedDebounceMs, ct);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Dispose()
    {
        _processPollCts?.Cancel();
        _processPollCts?.Dispose();
        _stateChangedDebounce?.Cancel();
        _stateChangedDebounce?.Dispose();
        _backend.StreamCreated -= OnStreamCreated;
        _backend.StreamRemoved -= OnStreamRemoved;
        _backend.StreamChanged -= OnStreamChanged;
        _backend.DeviceChanged -= OnDeviceChanged;
    }
}
