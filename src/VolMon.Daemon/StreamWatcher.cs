using VolMon.Core.Audio;
using VolMon.Core.Config;

namespace VolMon.Daemon;

/// <summary>
/// Tracks the PA module index and stable sink name for a CompatibilityMode virtual sink.
/// </summary>
internal sealed record VirtualSinkInfo(uint ModuleIndex, string SinkName);

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
    /// Per-group virtual sink state for <see cref="GroupMode.Compatibility"/> groups.
    /// Key: group ID. Value: the PA module index and stable sink name.
    /// The virtual sink is created when a group's mode first becomes Compatibility
    /// (or at startup for pre-configured groups) and destroyed when the mode reverts
    /// to Direct or the group is deleted.
    /// </summary>
    private readonly Dictionary<Guid, VirtualSinkInfo> _virtualSinks = [];

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

    // Name prefix for VolMon null-sink virtual devices.
    private const string VirtualSinkPrefix = "volmon_compat_";

    /// <summary>
    /// When <c>true</c>, if the virtual sink is unavailable for a Compatibility group,
    /// streams fall back to direct volume control. When <c>false</c>, streams are left
    /// untouched and a warning is logged instead.
    /// </summary>
    private const bool FallbackToDirectVolume = false;

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
    /// For groups in CompatibilityMode, a virtual null-sink is created first so that
    /// any existing streams can be routed into it.
    /// </summary>
    public async Task InitialScanAsync(CancellationToken ct = default)
    {
        // Create virtual sinks for all CompatibilityMode groups before scanning streams,
        // so that ApplyStreamSettingsAsync can route new streams into them immediately.
        foreach (var group in _configManager.Config.Groups)
        {
            if (group.Mode == GroupMode.Compatibility)
                await EnsureVirtualSinkAsync(group, ct);
        }

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
    /// Applies volume/mute settings to the specified group.
    /// In CompatibilityMode the virtual sink volume is set via the native PipeWire API
    /// and individual streams are left untouched; in Direct mode the stream volume is
    /// set directly.
    /// </summary>
    public async Task ApplyGroupSettingsAsync(AudioGroup group, CancellationToken ct = default)
    {
        if (group.Mode == GroupMode.Compatibility)
        {
            // Ensure the virtual sink exists (it may not if mode just changed).
            await EnsureVirtualSinkAsync(group, ct);

            if (_virtualSinks.TryGetValue(group.Id, out var vsink))
            {
                // Control volume/mute on the virtual sink via the native PipeWire API.
                // Individual stream volumes are left untouched.
                try
                {
                    await _backend.SetVirtualSinkVolumeAsync(vsink.SinkName, group.Volume, ct);
                    await _backend.SetVirtualSinkMuteAsync(vsink.SinkName, group.Muted, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply settings to virtual sink {SinkName}", vsink.SinkName);
                }
            }
        }
        else
        {
            // Direct mode: set stream volume directly.
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
        }

        // Devices are always controlled by direct volume regardless of mode.
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
    /// Reconciles CompatibilityMode virtual sinks: creates missing ones, destroys
    /// ones no longer needed.
    /// </summary>
    public async Task ReassignAllAsync(CancellationToken ct = default)
    {
        // Reconcile virtual sinks: create for Compatibility groups, destroy for Direct groups.
        var currentCompatGroups = _configManager.Config.Groups
            .Where(g => g.Mode == GroupMode.Compatibility)
            .Select(g => g.Id)
            .ToHashSet();

        // Create sinks for newly Compatibility groups.
        foreach (var group in _configManager.Config.Groups)
        {
            if (group.Mode == GroupMode.Compatibility)
                await EnsureVirtualSinkAsync(group, ct);
        }

        // Destroy sinks for groups that are no longer Compatibility.
        foreach (var (groupId, vsink) in _virtualSinks.ToArray())
        {
            if (!currentCompatGroups.Contains(groupId))
                await TearDownVirtualSinkAsync(groupId, vsink, moveStreamsToDefault: true, ct);
        }

        // Snapshot to avoid "collection was modified" if pactl events fire concurrently
        foreach (var stream in _activeStreams.Values.ToArray())
        {
            var previousGroup = stream.AssignedGroup;
            stream.AssignedGroup = null;
            AssignStreamToGroup(stream);
            await ApplyStreamSettingsAsync(stream, ct);

            // If the stream was previously in a Compatibility group's virtual sink
            // but is now unassigned (e.g. moved to the ignored list), move it back
            // to the default output sink so it doesn't stay stranded on the virtual sink.
            if (stream.AssignedGroup is null
                && previousGroup is not null
                && _virtualSinks.ContainsKey(previousGroup.Value))
            {
                try
                {
                    await _backend.MoveStreamToSinkAsync(stream.Id, "@DEFAULT_SINK@", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Failed to move ignored stream {StreamId} back to default sink",
                        stream.Id);
                }
            }
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
            if (group.Mode == GroupMode.Compatibility)
            {
                await RouteStreamToVirtualSinkAsync(stream, group, ct);
            }
            else
            {
                // If this stream was previously routed to a virtual sink (e.g. it was
                // just removed from a Compatibility group), move it back to the default
                // output sink before applying direct volume control.
                await _backend.MoveStreamToSinkAsync(stream.Id, "@DEFAULT_SINK@", ct);

                await _backend.SetStreamVolumeAsync(stream.Id, group.Volume, ct);
                await _backend.SetStreamMuteAsync(stream.Id, group.Muted, ct);
                stream.Volume = group.Volume;
                stream.Muted = group.Muted;
            }
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

    // ── CompatibilityMode: virtual sink helpers ───────────────────────

    /// <summary>
    /// Returns a stable, filesystem-safe sink name for the given group.
    /// The GUID ensures uniqueness even if two groups share the same display name.
    /// </summary>
    private static string VirtualSinkNameFor(AudioGroup group) =>
        $"{VirtualSinkPrefix}{group.Id:N}";

    /// <summary>
    /// Ensures a null-sink virtual device exists for <paramref name="group"/>.
    /// If one is already tracked, this is a no-op. Otherwise the PA module is loaded
    /// and the result recorded in <see cref="_virtualSinks"/>.
    /// </summary>
    private async Task EnsureVirtualSinkAsync(AudioGroup group, CancellationToken ct = default)
    {
        if (_virtualSinks.ContainsKey(group.Id)) return;

        var sinkName = VirtualSinkNameFor(group);
        _logger.LogInformation(
            "Creating virtual null-sink '{SinkName}' for CompatibilityMode group '{Group}'",
            sinkName, group.Name);

        var moduleIndex = await _backend.CreateVirtualSinkAsync(
            sinkName, $"VolMon: {group.Name}", ct);

        if (moduleIndex is null)
        {
            _logger.LogWarning(
                "Backend does not support virtual sinks — CompatibilityMode unavailable for group '{Group}'",
                group.Name);
            return;
        }

        _virtualSinks[group.Id] = new VirtualSinkInfo(moduleIndex.Value, sinkName);
        _logger.LogInformation(
            "Virtual sink '{SinkName}' created (handle {Idx}) for group '{Group}'",
            sinkName, moduleIndex.Value, group.Name);

        // Apply the current group volume to the new sink via the PulseAudio API.
        try
        {
            await _backend.SetVirtualSinkVolumeAsync(sinkName, group.Volume, ct);
            await _backend.SetVirtualSinkMuteAsync(sinkName, group.Muted, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set initial volume on virtual sink '{SinkName}'", sinkName);
        }
    }

    /// <summary>
    /// Moves a stream into the group's virtual null-sink. The stream's individual
    /// volume is left untouched — only the virtual sink's volume matters.
    /// </summary>
    private async Task RouteStreamToVirtualSinkAsync(
        AudioStream stream, AudioGroup group, CancellationToken ct = default)
    {
        if (!_virtualSinks.TryGetValue(group.Id, out var vsink))
        {
            if (FallbackToDirectVolume)
            {
                // Sink not available — fall back to direct volume control.
                await _backend.SetStreamVolumeAsync(stream.Id, group.Volume, ct);
                await _backend.SetStreamMuteAsync(stream.Id, group.Muted, ct);
                stream.Volume = group.Volume;
                stream.Muted = group.Muted;
            }
            else
            {
                _logger.LogWarning(
                    "Virtual sink for group '{Group}' ({GroupId}) is not available; "
                    + "stream {StreamId} left untouched",
                    group.Name, group.Id, stream.Id);
            }

            return;
        }

        try
        {
            await _backend.MoveStreamToSinkAsync(stream.Id, vsink.SinkName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to route stream {StreamId} to virtual sink '{SinkName}'",
                stream.Id, vsink.SinkName);
        }
    }

    /// <summary>
    /// Destroys a group's virtual null-sink and optionally moves all its streams
    /// back to the system default sink.
    /// </summary>
    private async Task TearDownVirtualSinkAsync(
        Guid groupId, VirtualSinkInfo vsink, bool moveStreamsToDefault, CancellationToken ct = default)
    {
        if (moveStreamsToDefault)
        {
            // Move all streams that are still inside this virtual sink back to the default.
            var streams = _activeStreams.Values
                .Where(s => s.AssignedGroup == groupId)
                .ToArray();

            foreach (var stream in streams)
            {
                try
                {
                    // "@DEFAULT_SINK@" is a PulseAudio magic name that always resolves
                    // to the current default output sink.
                    await _backend.MoveStreamToSinkAsync(stream.Id, "@DEFAULT_SINK@", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Failed to move stream {StreamId} back to default sink", stream.Id);
                }
            }
        }

        try
        {
            await _backend.DestroyVirtualSinkAsync(vsink.ModuleIndex, ct);
            _logger.LogInformation(
                "Destroyed virtual sink '{SinkName}' (module {Idx})",
                vsink.SinkName, vsink.ModuleIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to unload virtual sink module {Idx}", vsink.ModuleIndex);
        }

        _virtualSinks.Remove(groupId);
    }

    // ── Debounced state notification ─────────────────────────────────

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

        // Best-effort teardown of virtual sinks on shutdown (fire-and-forget).
        foreach (var (groupId, vsink) in _virtualSinks.ToArray())
        {
            _ = TearDownVirtualSinkAsync(groupId, vsink, moveStreamsToDefault: true,
                CancellationToken.None);
        }
    }
}
