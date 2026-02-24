using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static VolMon.Core.Audio.Backends.LibCoreAudio;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// macOS audio backend using CoreAudio HAL via P/Invoke.
///
/// <para><b>Device enumeration and control</b> is fully supported — volume,
/// mute, and monitoring for hardware sinks (output) and sources (input).</para>
///
/// <para><b>Per-app streams</b> are NOT natively available on macOS. CoreAudio
/// does not expose per-application audio sessions. Each audio stream that macOS
/// does surface (if any future API support is added) is treated as its own
/// "process" since we cannot attribute streams to OS processes.</para>
///
/// <para><b>Virtual sinks</b> are not supported — would require a third-party
/// virtual audio driver (e.g. BlackHole). All virtual sink methods are no-ops.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsBackend : IAudioBackend
{
    private volatile bool _monitoring;
    private volatile bool _disposed;

    // Pinned delegates to prevent GC while native listeners are active.
    private AudioObjectPropertyListenerProc? _deviceListListener;
    private AudioObjectPropertyListenerProc? _defaultOutputListener;
    private AudioObjectPropertyListenerProc? _defaultInputListener;

    // Per-device listeners keyed by device ID.
    private readonly Dictionary<uint, List<AudioObjectPropertyListenerProc>> _deviceListeners = new();
    private readonly object _listenerLock = new();

    // Cache of known device IDs for detecting adds/removes.
    private HashSet<uint> _knownDeviceIds = new();

    // ── Events ───────────────────────────────────────────────────────

#pragma warning disable CS0067 // Stream events not raised — macOS does not expose per-app audio sessions
    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
#pragma warning restore CS0067
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;
    public event EventHandler<DefaultSinkChangedEventArgs>? DefaultSinkChanged;

    // ── Streams (not supported on macOS) ─────────────────────────────

    /// <summary>
    /// macOS CoreAudio does not expose per-application streams.
    /// Returns an empty list.
    /// </summary>
    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioStream>>(Array.Empty<AudioStream>());

    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) =>
        Task.CompletedTask;

    // ── Processes (not supported on macOS) ────────────────────────────

    /// <summary>
    /// macOS CoreAudio does not expose per-application audio sessions,
    /// so we cannot attribute streams to processes. Returns an empty list.
    /// </summary>
    public Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioProcess>>(Array.Empty<AudioProcess>());

    // ── Devices ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<AudioDevice>();
        var deviceIds = GetAudioObjectArray(
            kAudioObjectSystemObject,
            kAudioHardwarePropertyDevices);

        if (deviceIds is null)
            return Task.FromResult<IReadOnlyList<AudioDevice>>(devices);

        foreach (var deviceId in deviceIds)
        {
            // Check if device is alive
            var alive = GetUInt32Property(deviceId, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal);
            if (alive.HasValue && alive.Value == 0)
                continue;

            var uid = GetStringProperty(deviceId, kAudioDevicePropertyDeviceUID);
            var name = GetStringProperty(deviceId, kAudioObjectPropertyName);

            if (string.IsNullOrEmpty(uid))
                continue;

            // Determine if this device has output streams, input streams, or both.
            var outputStreams = GetAudioObjectArray(deviceId, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput);
            var inputStreams = GetAudioObjectArray(deviceId, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput);

            bool hasOutput = outputStreams is { Length: > 0 };
            bool hasInput = inputStreams is { Length: > 0 };

            if (hasOutput)
            {
                var (volume, muted) = GetDeviceVolumeAndMute(deviceId, kAudioObjectPropertyScopeOutput);
                devices.Add(new AudioDevice
                {
                    Id = deviceId.ToString(),
                    Name = uid,
                    Description = name,
                    Type = DeviceType.Sink,
                    Volume = volume,
                    Muted = muted
                });
            }

            if (hasInput)
            {
                var (volume, muted) = GetDeviceVolumeAndMute(deviceId, kAudioObjectPropertyScopeInput);
                // Use a suffix to distinguish input from output when both exist on the same device
                var inputUid = hasOutput ? uid + ":input" : uid;
                devices.Add(new AudioDevice
                {
                    Id = deviceId.ToString(),
                    Name = inputUid,
                    Description = (name ?? uid) + " (Input)",
                    Type = DeviceType.Source,
                    Volume = volume,
                    Muted = muted
                });
            }
        }

        return Task.FromResult<IReadOnlyList<AudioDevice>>(devices);
    }

    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        var (deviceId, scope) = ResolveDeviceFromName(deviceName);
        if (deviceId == 0) return Task.CompletedTask;

        var scalar = volume / 100f;

        // Try virtual master volume first (preserves stereo balance)
        var status = SetFloat32Property(deviceId, kAudioDevicePropertyVirtualMasterVolume, scope, scalar);
        if (status == kAudioHardwareNoError)
            return Task.CompletedTask;

        // Fall back to per-channel volume scalar on channels 1 and 2
        SetFloat32Property(deviceId, kAudioDevicePropertyVolumeScalar, scope, scalar, element: 1);
        SetFloat32Property(deviceId, kAudioDevicePropertyVolumeScalar, scope, scalar, element: 2);

        return Task.CompletedTask;
    }

    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default)
    {
        var (deviceId, scope) = ResolveDeviceFromName(deviceName);
        if (deviceId == 0) return Task.CompletedTask;

        SetUInt32Property(deviceId, kAudioDevicePropertyMute, scope, muted ? 1u : 0u);
        return Task.CompletedTask;
    }

    // ── Monitoring ───────────────────────────────────────────────────

    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        if (_monitoring) return Task.CompletedTask;
        _monitoring = true;

        // Snapshot current devices
        var deviceIds = GetAudioObjectArray(kAudioObjectSystemObject, kAudioHardwarePropertyDevices);
        _knownDeviceIds = deviceIds is not null ? new HashSet<uint>(deviceIds) : new HashSet<uint>();

        // Listen for device list changes (add/remove)
        _deviceListListener = OnDeviceListChanged;
        var devicesAddr = new AudioObjectPropertyAddress(
            kAudioHardwarePropertyDevices,
            kAudioObjectPropertyScopeGlobal,
            kAudioObjectPropertyElementMain);
        AudioObjectAddPropertyListener(kAudioObjectSystemObject, ref devicesAddr, _deviceListListener, IntPtr.Zero);

        // Listen for default output device changes
        _defaultOutputListener = OnDefaultOutputChanged;
        var defaultOutAddr = new AudioObjectPropertyAddress(
            kAudioHardwarePropertyDefaultOutputDevice,
            kAudioObjectPropertyScopeGlobal,
            kAudioObjectPropertyElementMain);
        AudioObjectAddPropertyListener(kAudioObjectSystemObject, ref defaultOutAddr, _defaultOutputListener, IntPtr.Zero);

        // Listen for default input device changes
        _defaultInputListener = OnDefaultInputChanged;
        var defaultInAddr = new AudioObjectPropertyAddress(
            kAudioHardwarePropertyDefaultInputDevice,
            kAudioObjectPropertyScopeGlobal,
            kAudioObjectPropertyElementMain);
        AudioObjectAddPropertyListener(kAudioObjectSystemObject, ref defaultInAddr, _defaultInputListener, IntPtr.Zero);

        // Install per-device listeners for volume/mute on all current devices
        if (deviceIds is not null)
        {
            foreach (var id in deviceIds)
                InstallDeviceListeners(id);
        }

        return Task.CompletedTask;
    }

    public Task StopMonitoringAsync()
    {
        if (!_monitoring) return Task.CompletedTask;
        _monitoring = false;

        // Remove system-level listeners
        if (_deviceListListener is not null)
        {
            var addr = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDevices,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMain);
            AudioObjectRemovePropertyListener(kAudioObjectSystemObject, ref addr, _deviceListListener, IntPtr.Zero);
        }

        if (_defaultOutputListener is not null)
        {
            var addr = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultOutputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMain);
            AudioObjectRemovePropertyListener(kAudioObjectSystemObject, ref addr, _defaultOutputListener, IntPtr.Zero);
        }

        if (_defaultInputListener is not null)
        {
            var addr = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultInputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMain);
            AudioObjectRemovePropertyListener(kAudioObjectSystemObject, ref addr, _defaultInputListener, IntPtr.Zero);
        }

        // Remove all per-device listeners
        lock (_listenerLock)
        {
            foreach (var (deviceId, listeners) in _deviceListeners)
                RemoveDeviceListenersUnsafe(deviceId, listeners);
            _deviceListeners.Clear();
        }

        return Task.CompletedTask;
    }

    // ── Virtual sinks (not supported on macOS) ───────────────────────

    public Task<uint?> CreateVirtualSinkAsync(string sinkName, string description, CancellationToken ct = default) =>
        Task.FromResult<uint?>(null);

    public Task DestroyVirtualSinkAsync(uint moduleIndex, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task MoveStreamToSinkAsync(string streamId, string sinkName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetVirtualSinkVolumeAsync(string sinkName, int volume, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SetVirtualSinkMuteAsync(string sinkName, bool muted, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RelinkVirtualSinkAsync(string virtualSinkName, string targetSinkName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<string?> GetDefaultSinkNameAsync(CancellationToken ct = default)
    {
        var addr = new AudioObjectPropertyAddress(
            kAudioHardwarePropertyDefaultOutputDevice,
            kAudioObjectPropertyScopeGlobal,
            kAudioObjectPropertyElementMain);

        uint dataSize = sizeof(uint);
        var ptr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            var status = AudioObjectGetPropertyData(
                kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref dataSize, ptr);
            if (status != kAudioHardwareNoError)
                return Task.FromResult<string?>(null);

            var deviceId = (uint)Marshal.ReadInt32(ptr);
            var uid = GetStringProperty(deviceId, kAudioDevicePropertyDeviceUID);
            return Task.FromResult(uid);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_monitoring)
            StopMonitoringAsync().GetAwaiter().GetResult();
    }

    // ── Private: volume / mute helpers ───────────────────────────────

    /// <summary>
    /// Reads volume and mute for a device in the given scope.
    /// Uses virtual master volume first, falls back to channel 1 volume scalar.
    /// </summary>
    private static (int Volume, bool Muted) GetDeviceVolumeAndMute(uint deviceId, uint scope)
    {
        // Volume: try virtual master first
        var vol = GetFloat32Property(deviceId, kAudioDevicePropertyVirtualMasterVolume, scope);
        if (!vol.HasValue)
        {
            // Fall back to channel 1 volume
            vol = GetFloat32Property(deviceId, kAudioDevicePropertyVolumeScalar, scope, element: 1);
        }

        int volume = vol.HasValue ? Math.Clamp((int)Math.Round(vol.Value * 100), 0, 100) : 100;

        // Mute
        var muteVal = GetUInt32Property(deviceId, kAudioDevicePropertyMute, scope);
        bool muted = muteVal.HasValue && muteVal.Value != 0;

        return (volume, muted);
    }

    /// <summary>
    /// Resolves a device UID (our Name field) to a CoreAudio device ID and scope.
    /// Handles the ":input" suffix convention for input devices that share a UID
    /// with an output device.
    /// </summary>
    private static (uint DeviceId, uint Scope) ResolveDeviceFromName(string deviceName)
    {
        bool isInput = deviceName.EndsWith(":input", StringComparison.Ordinal);
        var uid = isInput ? deviceName[..^":input".Length] : deviceName;
        var scope = isInput ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput;

        var deviceIds = GetAudioObjectArray(kAudioObjectSystemObject, kAudioHardwarePropertyDevices);
        if (deviceIds is null) return (0, scope);

        foreach (var id in deviceIds)
        {
            var devUid = GetStringProperty(id, kAudioDevicePropertyDeviceUID);
            if (string.Equals(devUid, uid, StringComparison.Ordinal))
                return (id, scope);
        }

        return (0, scope);
    }

    // ── Private: monitoring callbacks ────────────────────────────────

    /// <summary>
    /// Called when the system device list changes (device plugged/unplugged).
    /// Dispatches to the thread pool to avoid re-entrancy in CoreAudio callbacks.
    /// </summary>
    private int OnDeviceListChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        if (!_monitoring) return 0;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var currentIds = GetAudioObjectArray(kAudioObjectSystemObject, kAudioHardwarePropertyDevices);
                var currentSet = currentIds is not null ? new HashSet<uint>(currentIds) : new HashSet<uint>();

                // Detect removed devices
                foreach (var oldId in _knownDeviceIds)
                {
                    if (!currentSet.Contains(oldId))
                    {
                        RemoveDeviceListeners(oldId);
                        var uid = GetStringProperty(oldId, kAudioDevicePropertyDeviceUID);
                        if (uid is not null)
                        {
                            DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                            {
                                DeviceName = uid,
                                EventType = AudioDeviceEventType.Removed
                            });
                        }
                    }
                }

                // Detect added devices
                foreach (var newId in currentSet)
                {
                    if (!_knownDeviceIds.Contains(newId))
                    {
                        InstallDeviceListeners(newId);
                        var uid = GetStringProperty(newId, kAudioDevicePropertyDeviceUID);
                        if (uid is not null)
                        {
                            DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                            {
                                DeviceName = uid,
                                EventType = AudioDeviceEventType.Added
                            });
                        }
                    }
                }

                _knownDeviceIds = currentSet;
            }
            catch
            {
                // Best effort — don't crash on monitoring errors
            }
        });

        return 0;
    }

    /// <summary>
    /// Called when the default output device changes.
    /// </summary>
    private int OnDefaultOutputChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        if (!_monitoring) return 0;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var sinkName = GetDefaultSinkNameAsync().GetAwaiter().GetResult();
                if (sinkName is not null)
                {
                    DefaultSinkChanged?.Invoke(this, new DefaultSinkChangedEventArgs
                    {
                        SinkName = sinkName
                    });
                }
            }
            catch { /* best effort */ }
        });

        return 0;
    }

    /// <summary>
    /// Called when the default input device changes. Fires a DeviceChanged event.
    /// </summary>
    private int OnDefaultInputChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        if (!_monitoring) return 0;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var addr = new AudioObjectPropertyAddress(
                    kAudioHardwarePropertyDefaultInputDevice,
                    kAudioObjectPropertyScopeGlobal,
                    kAudioObjectPropertyElementMain);

                uint dataSize = sizeof(uint);
                var ptr = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    var status = AudioObjectGetPropertyData(
                        kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref dataSize, ptr);
                    if (status != kAudioHardwareNoError) return;

                    var deviceId = (uint)Marshal.ReadInt32(ptr);
                    var uid = GetStringProperty(deviceId, kAudioDevicePropertyDeviceUID);
                    if (uid is not null)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                        {
                            DeviceName = uid + ":input",
                            EventType = AudioDeviceEventType.Changed
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch { /* best effort */ }
        });

        return 0;
    }

    /// <summary>
    /// Called when a per-device property changes (volume or mute).
    /// </summary>
    private int OnDevicePropertyChanged(uint deviceId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        if (!_monitoring) return 0;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var uid = GetStringProperty(deviceId, kAudioDevicePropertyDeviceUID);
                if (uid is null) return;

                // Determine which scopes changed and fire events
                var outputStreams = GetAudioObjectArray(deviceId, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput);
                var inputStreams = GetAudioObjectArray(deviceId, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput);

                if (outputStreams is { Length: > 0 })
                {
                    DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                    {
                        DeviceName = uid,
                        EventType = AudioDeviceEventType.Changed
                    });
                }

                if (inputStreams is { Length: > 0 })
                {
                    var inputName = outputStreams is { Length: > 0 } ? uid + ":input" : uid;
                    DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                    {
                        DeviceName = inputName,
                        EventType = AudioDeviceEventType.Changed
                    });
                }
            }
            catch { /* best effort */ }
        });

        return 0;
    }

    // ── Private: per-device listener management ──────────────────────

    /// <summary>
    /// Installs volume and mute property listeners on a device for both
    /// output and input scopes.
    /// </summary>
    private void InstallDeviceListeners(uint deviceId)
    {
        var listeners = new List<AudioObjectPropertyListenerProc>();

        // We listen for volume and mute on both output and input scopes.
        // CoreAudio will silently ignore listeners on properties that don't
        // exist for a given scope, so it's safe to install on both.
        uint[] scopes = [kAudioObjectPropertyScopeOutput, kAudioObjectPropertyScopeInput];
        uint[] selectors =
        [
            kAudioDevicePropertyVolumeScalar,
            kAudioDevicePropertyVirtualMasterVolume,
            kAudioDevicePropertyMute
        ];

        foreach (var scope in scopes)
        {
            foreach (var selector in selectors)
            {
                var addr = new AudioObjectPropertyAddress(selector, scope, kAudioObjectPropertyElementMain);
                if (!AudioObjectHasProperty(deviceId, ref addr))
                    continue;

                // Each listener delegate must be kept alive for the lifetime of the listener
                AudioObjectPropertyListenerProc listener = OnDevicePropertyChanged;
                var status = AudioObjectAddPropertyListener(deviceId, ref addr, listener, IntPtr.Zero);
                if (status == kAudioHardwareNoError)
                    listeners.Add(listener);
            }
        }

        if (listeners.Count > 0)
        {
            lock (_listenerLock)
            {
                _deviceListeners[deviceId] = listeners;
            }
        }
    }

    /// <summary>
    /// Removes all property listeners for a device.
    /// </summary>
    private void RemoveDeviceListeners(uint deviceId)
    {
        lock (_listenerLock)
        {
            if (_deviceListeners.TryGetValue(deviceId, out var listeners))
            {
                RemoveDeviceListenersUnsafe(deviceId, listeners);
                _deviceListeners.Remove(deviceId);
            }
        }
    }

    /// <summary>
    /// Removes all property listeners for a device. Must be called under <see cref="_listenerLock"/>.
    /// </summary>
    private static void RemoveDeviceListenersUnsafe(uint deviceId, List<AudioObjectPropertyListenerProc> listeners)
    {
        uint[] scopes = [kAudioObjectPropertyScopeOutput, kAudioObjectPropertyScopeInput];
        uint[] selectors =
        [
            kAudioDevicePropertyVolumeScalar,
            kAudioDevicePropertyVirtualMasterVolume,
            kAudioDevicePropertyMute
        ];

        foreach (var listener in listeners)
        {
            foreach (var scope in scopes)
            {
                foreach (var selector in selectors)
                {
                    var addr = new AudioObjectPropertyAddress(selector, scope, kAudioObjectPropertyElementMain);
                    // Best effort — ignore errors on removal
                    AudioObjectRemovePropertyListener(deviceId, ref addr, listener, IntPtr.Zero);
                }
            }
        }
    }
}
