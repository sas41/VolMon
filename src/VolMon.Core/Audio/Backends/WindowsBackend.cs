 using System.Diagnostics;
using VolMon.Core.Audio;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Windows audio backend using the Core Audio API via NAudio.
/// Provides per-application session control and device management through
/// MMDeviceEnumerator and IAudioSessionManager2.
///
/// Threading: COM objects are created on a dedicated STA thread with a message
/// pump so that session/device notifications are delivered reliably.
/// All public methods marshal work to this thread via a task queue.
/// </summary>
[SupportedOSPlatform("windows")]
 public sealed class WindowsBackend : IAudioBackend
{
    private Thread? _staThread;
    private BlockingTaskQueue? _queue;
    private CancellationTokenSource? _cts;

    // COM objects — only accessed on the STA thread
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotificationClient? _deviceNotifier;

    // Track sessions per device for event cleanup
    private readonly Dictionary<string, TrackedDevice> _trackedDevices = new();
    // Track live process watchers to cleanup streams when the associated
    // process exits, even if session notifications are missed.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process> _processWatchers = new();
    // Track processes and their streams for GUI consumption
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, AudioProcess> _processes = new();

    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
#pragma warning disable CS0067 // StreamChanged not raised — session volume changes are polled, not pushed
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
#pragma warning restore CS0067
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    // ── Streams ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            var streams = new List<AudioStream>();
            if (_enumerator is null) return (IReadOnlyList<AudioStream>)streams;

            // Enumerate sessions on all active render devices
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            var stream = SessionToStream(session);
                            if (stream is not null)
                                streams.Add(stream);
                        }
                        catch { /* skip inaccessible session */ }
                    }
                }
                catch { /* skip device without session manager */ }
            }

            return (IReadOnlyList<AudioStream>)streams;
        });

    /// <inheritdoc/>
    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            volume = Math.Clamp(volume, 0, 100);
            var session = FindSession(streamId) ?? FindSessionFallback(streamId);
            if (session is not null)
                session.SimpleAudioVolume.Volume = volume / 100f;
        });

    /// <inheritdoc/>
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            var session = FindSession(streamId) ?? FindSessionFallback(streamId);
            if (session is not null)
                session.SimpleAudioVolume.Mute = muted;
        });

    // ── Devices ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            var devices = new List<AudioDevice>();
            if (_enumerator is null) return (IReadOnlyList<AudioDevice>)devices;

            AddDevices(devices, DataFlow.Render, DeviceType.Sink);
            AddDevices(devices, DataFlow.Capture, DeviceType.Source);

            return (IReadOnlyList<AudioDevice>)devices;
        });

    /// <inheritdoc/>
    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            volume = Math.Clamp(volume, 0, 100);
            var device = FindDevice(deviceName);
            if (device is not null)
                device.AudioEndpointVolume.MasterVolumeLevelScalar = volume / 100f;
        });

    /// <inheritdoc/>
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default) =>
        RunOnStaAsync(() =>
        {
            var device = FindDevice(deviceName);
            if (device is not null)
                device.AudioEndpointVolume.Mute = muted;
        });

    // ── Monitoring ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _queue = new BlockingTaskQueue();

        _staThread = new Thread(StaThreadProc)
        {
            Name = "VolMon-CoreAudio-STA",
            IsBackground = true
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        // Wait for initialization to complete on the STA thread
        return RunOnStaAsync(() =>
        {
            _enumerator = new MMDeviceEnumerator();

            // Register for device add/remove/change notifications
            _deviceNotifier = new DeviceNotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_deviceNotifier);

            // Set up session monitoring on all active render devices
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                TrackDevice(device);
            }
        });
    }

    /// <inheritdoc/>
    public async Task StopMonitoringAsync()
    {
        _cts?.Cancel();
        _queue?.Complete();

        if (_staThread is not null && _staThread.IsAlive)
            _staThread.Join(timeout: TimeSpan.FromSeconds(3));
    }

    // ── STA thread ───────────────────────────────────────────────────

    private void StaThreadProc()
    {
        try
        {
            _queue!.ProcessUntilComplete();
        }
        finally
        {
            // Cleanup COM objects on the STA thread that created them
            CleanupTrackedDevices();

            if (_enumerator is not null && _deviceNotifier is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifier); }
                catch { /* already disposed */ }
            }

            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    // ── Private: session tracking ────────────────────────────────────

    private void TrackDevice(MMDevice device)
    {
        try
        {
            var sessionManager = device.AudioSessionManager;
            var handler = new SessionCreatedHandler(this, device.ID);
            sessionManager.OnSessionCreated += handler.OnSessionCreated;

            _trackedDevices[device.ID] = new TrackedDevice(device, sessionManager, handler);
        }
        catch { /* device may not support sessions */ }
    }

    private void CleanupTrackedDevices()
    {
        foreach (var tracked in _trackedDevices.Values)
        {
            try { tracked.Dispose(); }
            catch { /* best effort */ }
        }
        _trackedDevices.Clear();
    }

    /// <summary>
    /// Converts an NAudio AudioSessionControl to our AudioStream model.
    /// Returns null for system-sounds sessions or sessions without a valid PID.
    /// </summary>
    private static AudioStream? SessionToStream(AudioSessionControl session)
    {
        var pid = (int)session.GetProcessID;
        if (pid == 0) return null; // System sounds or aggregate session

        var binaryName = ResolveProcessName(pid) ?? "unknown";
        var volume = (int)Math.Round(session.SimpleAudioVolume.Volume * 100);
        var muted = session.SimpleAudioVolume.Mute;

        return new AudioStream
        {
            // Use PID as the stream ID — Windows sessions are 1:1 with processes
            // for most applications. The session instance ID is too volatile.
            Id = pid.ToString(),
            BinaryName = binaryName,
            ApplicationClass = session.DisplayName,
            Volume = Math.Clamp(volume, 0, 100),
            Muted = muted,
            ProcessId = pid
        };
    }

    /// <summary>
    /// Resolves a process name from its PID.
    /// </summary>
    private static string? ResolveProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    // Ensure a per-process AudioProcess entry exists
    private AudioProcess EnsureProcess(int pid)
    {
        return _processes.GetOrAdd(pid, p => new AudioProcess
        {
            Id = p,
            Name = ResolveProcessName(p) ?? "unknown",
            Streams = new List<AudioStream>()
        });
    }

    // Expose a list of processes with their streams
    public Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default) =>
        RunOnStaAsync<IReadOnlyList<AudioProcess>>(() =>
        {
            var list = _processes.Values.ToList<AudioProcess>();
            return (IReadOnlyList<AudioProcess>)list;
        });

    // CompatibilityMode not supported on Windows — WASAPI has no null-sink concept.
    public Task<uint?> CreateVirtualSinkAsync(string sinkName, string description, CancellationToken ct = default) =>
        Task.FromResult<uint?>(null);
    public Task DestroyVirtualSinkAsync(uint moduleIndex, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveStreamToSinkAsync(string streamId, string sinkName, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetVirtualSinkVolumeAsync(string sinkName, int volume, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetVirtualSinkMuteAsync(string sinkName, bool muted, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Finds a session by stream ID (PID string) across already-tracked devices.
    /// Uses <see cref="_trackedDevices"/> to avoid re-enumerating all audio
    /// endpoints on every volume/mute change.
    /// </summary>
    private AudioSessionControl? FindSession(string streamId)
    {
        if (!int.TryParse(streamId, out var targetPid)) return null;

        foreach (var tracked in _trackedDevices.Values)
        {
            try
            {
                var sessions = tracked.Device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if ((int)session.GetProcessID == targetPid)
                            return session;
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }

        return null;
    }

    // Fallback search across all endpoints to locate a session by PID
    private AudioSessionControl? FindSessionFallback(string streamId)
    {
        if (!int.TryParse(streamId, out var targetPid)) return null;

        // 1) Check already-tracked devices
        foreach (var tracked in _trackedDevices.Values)
        {
            try
            {
                var sessions = tracked.Device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if ((int)session.GetProcessID == targetPid)
                            return session;
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }

        // 2) Scan active render devices
        if (_enumerator != null)
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            if ((int)session.GetProcessID == targetPid)
                                return session;
                        }
                        catch { /* skip */ }
                    }
                }
                catch { /* skip device without session */ }
            }
        }

        return null;
    }

    // ── Private: device helpers ──────────────────────────────────────

    private void AddDevices(List<AudioDevice> list, DataFlow flow, DeviceType type)
    {
        if (_enumerator is null) return;

        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                var epVol = device.AudioEndpointVolume;
                var volume = (int)Math.Round(epVol.MasterVolumeLevelScalar * 100);

                list.Add(new AudioDevice
                {
                    Id = device.ID,
                    Name = device.ID, // Stable identifier
                    Description = device.FriendlyName,
                    Type = type,
                    Volume = Math.Clamp(volume, 0, 100),
                    Muted = epVol.Mute
                });
            }
            catch { /* skip inaccessible device */ }
        }
    }

    /// <summary>
    /// Finds a device by its ID (stable name stored in config).
    /// </summary>
    private MMDevice? FindDevice(string deviceName)
    {
        if (_enumerator is null) return null;

        try
        {
            return _enumerator.GetDevice(deviceName);
        }
        catch
        {
            return null;
        }
    }

    // ── Private: STA task queue ──────────────────────────────────────

    private Task RunOnStaAsync(Action action)
    {
        if (_queue is null)
            throw new InvalidOperationException("Backend not started. Call StartMonitoringAsync first.");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task<T> RunOnStaAsync<T>(Func<T> func)
    {
        if (_queue is null)
            throw new InvalidOperationException("Backend not started. Call StartMonitoringAsync first.");

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _queue?.Complete();

        if (_staThread is { IsAlive: true })
            _staThread.Join(timeout: TimeSpan.FromSeconds(3));

        _cts?.Dispose();
    }

    // ── Nested types ─────────────────────────────────────────────────

    /// <summary>
    /// Handles new session notifications from the audio session manager.
    /// </summary>
#pragma warning disable CS9113 // deviceId kept for future diagnostics
    private sealed class SessionCreatedHandler(WindowsBackend backend, string _deviceId)
#pragma warning restore CS9113
    {
        public void OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            try
            {
                var control = new AudioSessionControl(newSession);
                var pid = (int)control.GetProcessID;
                if (pid == 0) return;

                var pidStr = pid.ToString();

                // Register for session disconnect so we can fire StreamRemoved
                control.RegisterEventClient(new SessionEventClient(backend, pid));

                // Ensure per-process entry exists and attach stream if available
                var procModel = backend.EnsureProcess(pid);
                // Watch the corresponding OS process and remove the stream if it exits
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, e) =>
                    {
                        backend.StreamRemoved?.Invoke(backend, new AudioStreamEventArgs
                        {
                            StreamId = pidStr
                        });
                        // Remove from process streams
                        procModel.Streams.RemoveAll(x => x.Id == pidStr);
                        if (procModel.Streams.Count == 0)
                            backend._processes.TryRemove(pid, out _);
                        backend._processWatchers.TryRemove(pid, out _);
                    };
                    backend._processWatchers[pid] = proc;
                }
                catch { /* process may not be accessible yet; ignore */ }

                var stream = SessionToStream(control);
                if (stream is not null)
                    procModel.Streams.Add(stream);

                backend.StreamCreated?.Invoke(backend, new AudioStreamEventArgs
                {
                    StreamId = pidStr,
                    Stream = stream
                });
            }
            catch { /* ignore notification errors */ }
        }
    }

    /// <summary>
    /// Monitors an individual audio session for disconnect/state changes.
    /// Fires StreamRemoved when the session's process exits.
    /// </summary>
    private sealed class SessionEventClient(WindowsBackend backend, int pid) : IAudioSessionEventsHandler
    {
        private readonly string _pidString = pid.ToString();

        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnStateChanged(AudioSessionState state)
        {
            // Only treat expiration as a signal to remove the stream. Inactive
            // states may be transient while the app is still running.
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                backend.StreamRemoved?.Invoke(backend, new AudioStreamEventArgs
                {
                    StreamId = _pidString
                });
                // Cleanup any associated process watcher to avoid leaks
                if (int.TryParse(_pidString, out var pid))
                {
                    backend._processWatchers.TryRemove(pid, out _);
                    if (backend._processes.TryGetValue(pid, out var proc))
                    {
                        proc.Streams.RemoveAll(x => x.Id == _pidString);
                        if (proc.Streams.Count == 0)
                            backend._processes.TryRemove(pid, out _);
                    }
                }
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            // Treat disconnect as a potential end of stream; remove the stream
            // if it exists in the per-process list.
            if (int.TryParse(_pidString, out var pid))
            {
                if (backend._processes.TryGetValue(pid, out var proc))
                {
                    proc.Streams.RemoveAll(x => x.Id == _pidString);
                    backend.StreamRemoved?.Invoke(backend, new AudioStreamEventArgs
                    {
                        StreamId = _pidString
                    });
                    if (proc.Streams.Count == 0)
                        backend._processes.TryRemove(pid, out _);
                }
            }
        }
    }

    /// <summary>
    /// Handles device add/remove/change notifications.
    /// </summary>
    private sealed class DeviceNotificationClient(WindowsBackend backend) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            var eventType = newState == DeviceState.Active
                ? AudioDeviceEventType.Added
                : AudioDeviceEventType.Removed;

            backend.DeviceChanged?.Invoke(backend, new AudioDeviceEventArgs
            {
                DeviceName = deviceId,
                EventType = eventType
            });
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            backend.DeviceChanged?.Invoke(backend, new AudioDeviceEventArgs
            {
                DeviceName = pwstrDeviceId,
                EventType = AudioDeviceEventType.Added
            });
        }

        public void OnDeviceRemoved(string deviceId)
        {
            backend.DeviceChanged?.Invoke(backend, new AudioDeviceEventArgs
            {
                DeviceName = deviceId,
                EventType = AudioDeviceEventType.Removed
            });
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Treat default device change as a change event
            if (!string.IsNullOrEmpty(defaultDeviceId))
            {
                backend.DeviceChanged?.Invoke(backend, new AudioDeviceEventArgs
                {
                    DeviceName = defaultDeviceId,
                    EventType = AudioDeviceEventType.Changed
                });
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Ignore property changes — volume changes are handled per-session
        }
    }

    /// <summary>
    /// Tracks a device's session manager and event handler for cleanup.
    /// The sessionManager and handler parameters are stored to prevent GC
    /// of the COM callback delegate while monitoring is active.
    /// </summary>
#pragma warning disable CS9113 // Parameters kept alive to prevent GC of COM delegates
    private sealed class TrackedDevice(
        MMDevice device,
        AudioSessionManager _sessionManager,
        SessionCreatedHandler _handler) : IDisposable
#pragma warning restore CS9113
    {
        /// <summary>The underlying MMDevice (used for session lookups).</summary>
        public MMDevice Device => device;

        public void Dispose()
        {
            // NAudio doesn't expose a way to unsubscribe OnSessionCreated,
            // but disposing the device releases the COM reference.
            device.Dispose();
        }
    }

    /// <summary>
    /// Simple blocking queue for marshaling work to the STA thread.
    /// The STA thread calls <see cref="ProcessUntilComplete"/> which blocks
    /// and processes queued actions, providing a COM message pump via
    /// blocking collection waits.
    /// </summary>
    private sealed class BlockingTaskQueue
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _actions = new();

        public void Enqueue(Action action) => _actions.Add(action);

        public void Complete() => _actions.CompleteAdding();

        public void ProcessUntilComplete()
        {
            foreach (var action in _actions.GetConsumingEnumerable())
            {
                try { action(); }
                catch { /* errors are captured by TaskCompletionSource */ }
            }
        }
    }
}
