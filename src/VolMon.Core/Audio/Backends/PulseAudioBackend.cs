using System.Runtime.InteropServices;
using static VolMon.Core.Audio.Backends.LibPulse;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Linux audio backend using libpulse P/Invoke (works with both PulseAudio and PipeWire).
/// Maintains a persistent connection via <c>pa_threaded_mainloop</c> and communicates over
/// PulseAudio's native Unix-socket protocol — no process spawning or text parsing.
/// </summary>
/// <remarks>
/// Threading model:
/// <list type="bullet">
///   <item><c>pa_threaded_mainloop</c> runs its own background thread that drives all PA I/O.</item>
///   <item>All PA API calls from external threads must be wrapped in lock/unlock.</item>
///   <item>Callbacks from PA fire on the mainloop thread <b>with the lock already held</b> —
///         they must never call lock() again (assertion failure) and must not block.</item>
///   <item>Query methods (Get*Async) use the lock → issue op → wait → signal → unlock pattern
///         so the result is ready when the method returns.</item>
///   <item>Subscription callbacks dispatch events to the thread pool to avoid re-entrant
///         deadlocks when event handlers call back into the backend.</item>
/// </list>
/// </remarks>
public sealed class PulseAudioBackend : IAudioBackend
{
    private IntPtr _mainloop;
    private IntPtr _api;
    private IntPtr _context;
    private volatile bool _ready;
    private volatile bool _disposed;

    // Prevent GC collection of delegates passed to native code.
    // These must live as long as the context/mainloop they are registered with.
    private pa_context_notify_cb_t? _stateCallback;
    private pa_context_subscribe_cb_t? _subscribeCallback;

    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    // ── Connection lifecycle ─────────────────────────────────────────

    /// <summary>
    /// Connects to PulseAudio (or PipeWire-pulse) using the default server socket.
    /// Blocks until the context reaches READY or fails.
    /// </summary>
    private void EnsureConnected()
    {
        if (_ready) return;

        _mainloop = pa_threaded_mainloop_new();
        if (_mainloop == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create pa_threaded_mainloop.");

        _api = pa_threaded_mainloop_get_api(_mainloop);

        var proplist = pa_proplist_new();
        pa_proplist_sets(proplist, "application.name", "VolMon");
        pa_proplist_sets(proplist, "application.id", "com.volmon.daemon");
        pa_proplist_sets(proplist, "application.icon_name", "audio-card");

        _context = pa_context_new_with_proplist(_api, null, proplist);
        pa_proplist_free(proplist);

        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create pa_context.");

        _stateCallback = OnContextState;
        pa_context_set_state_callback(_context, _stateCallback, IntPtr.Zero);

        if (pa_context_connect(_context, null, pa_context_flags_t.NoFail, IntPtr.Zero) < 0)
            throw new InvalidOperationException(
                $"pa_context_connect failed: {GetError()}");

        if (pa_threaded_mainloop_start(_mainloop) < 0)
            throw new InvalidOperationException("Failed to start pa_threaded_mainloop.");

        // Wait for the context to become ready (or fail).
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            while (true)
            {
                var state = pa_context_get_state(_context);
                if (state == pa_context_state_t.Ready)
                {
                    _ready = true;
                    break;
                }
                if (state is pa_context_state_t.Failed or pa_context_state_t.Terminated)
                {
                    throw new InvalidOperationException(
                        $"PulseAudio connection failed (state={state}): {GetError()}");
                }

                pa_threaded_mainloop_wait(_mainloop);
            }
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    /// <summary>
    /// State callback — runs on the mainloop thread (lock already held).
    /// Just signals the waiting thread; never locks.
    /// </summary>
    private void OnContextState(IntPtr context, IntPtr userdata)
    {
        pa_threaded_mainloop_signal(_mainloop, 0);
    }

    private string GetError()
    {
        if (_context == IntPtr.Zero) return "no context";
        var errno = pa_context_errno(_context);
        var ptr = pa_strerror(errno);
        return PtrToStringUtf8(ptr) ?? $"error {errno}";
    }

    // ── Streams ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var streams = new List<AudioStream>();

        // Callback runs on mainloop thread (lock already held) — collect data, then signal.
        pa_sink_input_info_cb_t cb = (_, infoPtr, eol, _) =>
        {
            if (eol > 0)
            {
                pa_threaded_mainloop_signal(_mainloop, 0);
                return;
            }
            if (infoPtr == IntPtr.Zero) return;

            var info = Marshal.PtrToStructure<pa_sink_input_info>(infoPtr);
            var proplist = info.proplist;

            var appName = proplist != IntPtr.Zero
                ? PtrToStringUtf8(pa_proplist_gets(proplist, "application.name"))
                : null;
            var pidStr = proplist != IntPtr.Zero
                ? PtrToStringUtf8(pa_proplist_gets(proplist, "application.process.id"))
                : null;
            int? pid = pidStr is not null && int.TryParse(pidStr, out var p) ? p : null;

            var binaryName = ResolveProcessBinary(pid)
                ?? (proplist != IntPtr.Zero
                    ? PtrToStringUtf8(pa_proplist_gets(proplist, "application.process.binary"))
                    : null)
                ?? appName
                ?? "unknown";

            streams.Add(new AudioStream
            {
                Id = info.index.ToString(),
                BinaryName = binaryName,
                ApplicationClass = appName,
                Volume = CvolumeToPercent(ref info.volume),
                Muted = info.mute != 0,
                ProcessId = pid
            });
        };

        // Lock → issue → wait (callback signals when eol) → unlock.
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op = pa_context_get_sink_input_info_list(_context, cb, IntPtr.Zero);
            if (op == IntPtr.Zero)
                throw new InvalidOperationException($"pa_context_get_sink_input_info_list failed: {GetError()}");

            while (pa_operation_get_state(op) == pa_operation_state_t.Running)
                pa_threaded_mainloop_wait(_mainloop);

            pa_operation_unref(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        // GC.KeepAlive ensures the delegate isn't collected before the native side is done.
        GC.KeepAlive(cb);
        return Task.FromResult<IReadOnlyList<AudioStream>>(streams);
    }

    /// <inheritdoc/>
    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default)
    {
        EnsureConnected();
        if (!uint.TryParse(streamId, out var idx))
            throw new ArgumentException($"Invalid stream ID: {streamId}", nameof(streamId));

        var cvol = PercentToCvolume(2, volume);

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op = pa_context_set_sink_input_volume(_context, idx, ref cvol, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default)
    {
        EnsureConnected();
        if (!uint.TryParse(streamId, out var idx))
            throw new ArgumentException($"Invalid stream ID: {streamId}", nameof(streamId));

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op = pa_context_set_sink_input_mute(_context, idx, muted ? 1 : 0, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        return Task.CompletedTask;
    }

    // ── Devices ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var devices = new List<AudioDevice>();
        int pendingLists = 2; // sinks + sources

        pa_sink_info_cb_t sinkCb = (_, infoPtr, eol, _) =>
        {
            if (eol > 0)
            {
                if (Interlocked.Decrement(ref pendingLists) == 0)
                    pa_threaded_mainloop_signal(_mainloop, 0);
                return;
            }
            if (infoPtr == IntPtr.Zero) return;

            var info = Marshal.PtrToStructure<pa_sink_info>(infoPtr);
            devices.Add(new AudioDevice
            {
                Id = info.index.ToString(),
                Name = PtrToStringUtf8(info.name) ?? "",
                Description = PtrToStringUtf8(info.description),
                Type = DeviceType.Sink,
                Volume = CvolumeToPercent(ref info.volume),
                Muted = info.mute != 0
            });
        };

        pa_source_info_cb_t sourceCb = (_, infoPtr, eol, _) =>
        {
            if (eol > 0)
            {
                if (Interlocked.Decrement(ref pendingLists) == 0)
                    pa_threaded_mainloop_signal(_mainloop, 0);
                return;
            }
            if (infoPtr == IntPtr.Zero) return;

            var info = Marshal.PtrToStructure<pa_source_info>(infoPtr);
            var name = PtrToStringUtf8(info.name) ?? "";

            // Skip monitor sources (they mirror sinks, not real hardware).
            if (name.Contains(".monitor")) return;

            devices.Add(new AudioDevice
            {
                Id = info.index.ToString(),
                Name = name,
                Description = PtrToStringUtf8(info.description),
                Type = DeviceType.Source,
                Volume = CvolumeToPercent(ref info.volume),
                Muted = info.mute != 0
            });
        };

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op1 = pa_context_get_sink_info_list(_context, sinkCb, IntPtr.Zero);
            if (op1 == IntPtr.Zero)
                throw new InvalidOperationException($"pa_context_get_sink_info_list failed: {GetError()}");

            var op2 = pa_context_get_source_info_list(_context, sourceCb, IntPtr.Zero);
            if (op2 == IntPtr.Zero)
            {
                pa_operation_unref(op1);
                throw new InvalidOperationException($"pa_context_get_source_info_list failed: {GetError()}");
            }

            // Wait until both sink and source lists have delivered their eol.
            while (pa_operation_get_state(op1) == pa_operation_state_t.Running ||
                   pa_operation_get_state(op2) == pa_operation_state_t.Running)
            {
                pa_threaded_mainloop_wait(_mainloop);
            }

            pa_operation_unref(op1);
            pa_operation_unref(op2);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        GC.KeepAlive(sinkCb);
        GC.KeepAlive(sourceCb);
        return Task.FromResult<IReadOnlyList<AudioDevice>>(devices);
    }

    /// <inheritdoc/>
    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default)
    {
        EnsureConnected();
        var cvol = PercentToCvolume(2, volume);

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            // Fire both sink and source — the one that doesn't match the name simply
            // fails silently (no exception; the success callback is null).
            var op1 = pa_context_set_sink_volume_by_name(_context, deviceName, ref cvol, null, IntPtr.Zero);
            if (op1 != IntPtr.Zero) pa_operation_unref(op1);

            var op2 = pa_context_set_source_volume_by_name(_context, deviceName, ref cvol, null, IntPtr.Zero);
            if (op2 != IntPtr.Zero) pa_operation_unref(op2);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default)
    {
        EnsureConnected();
        var m = muted ? 1 : 0;

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            var op1 = pa_context_set_sink_mute_by_name(_context, deviceName, m, null, IntPtr.Zero);
            if (op1 != IntPtr.Zero) pa_operation_unref(op1);

            var op2 = pa_context_set_source_mute_by_name(_context, deviceName, m, null, IntPtr.Zero);
            if (op2 != IntPtr.Zero) pa_operation_unref(op2);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        return Task.CompletedTask;
    }

    // ── Monitoring ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        EnsureConnected();

        _subscribeCallback = OnSubscriptionEvent;

        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            pa_context_set_subscribe_callback(_context, _subscribeCallback, IntPtr.Zero);

            var mask = pa_subscription_mask_t.Sink
                     | pa_subscription_mask_t.Source
                     | pa_subscription_mask_t.SinkInput
                     | pa_subscription_mask_t.SourceOutput;

            var op = pa_context_subscribe(_context, mask, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopMonitoringAsync()
    {
        if (_context != IntPtr.Zero && _ready)
        {
            pa_threaded_mainloop_lock(_mainloop);
            try
            {
                pa_context_set_subscribe_callback(_context, null, IntPtr.Zero);
                var op = pa_context_subscribe(_context, pa_subscription_mask_t.Null, null, IntPtr.Zero);
                if (op != IntPtr.Zero) pa_operation_unref(op);
            }
            finally
            {
                pa_threaded_mainloop_unlock(_mainloop);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscription callback — runs on the mainloop thread (lock already held).
    /// <b>Must not</b> call lock/unlock or block. Dispatches events to the thread pool
    /// so that event handlers can safely call back into the backend (e.g. GetStreamsAsync)
    /// without causing a re-entrant deadlock on the mainloop lock.
    /// </summary>
    private void OnSubscriptionEvent(
        IntPtr context, pa_subscription_event_type_t type, uint idx, IntPtr userdata)
    {
        var facility = type & pa_subscription_event_type_t.FacilityMask;
        var eventKind = type & pa_subscription_event_type_t.TypeMask;
        var idStr = idx.ToString();

        switch (facility)
        {
            case pa_subscription_event_type_t.SinkInput:
            {
                var args = new AudioStreamEventArgs { StreamId = idStr };
                switch (eventKind)
                {
                    case pa_subscription_event_type_t.New:
                        ThreadPool.QueueUserWorkItem(_ => StreamCreated?.Invoke(this, args));
                        break;
                    case pa_subscription_event_type_t.Remove:
                        ThreadPool.QueueUserWorkItem(_ => StreamRemoved?.Invoke(this, args));
                        break;
                    case pa_subscription_event_type_t.Change:
                        ThreadPool.QueueUserWorkItem(_ => StreamChanged?.Invoke(this, args));
                        break;
                }
                break;
            }

            case pa_subscription_event_type_t.Sink:
            case pa_subscription_event_type_t.Source:
            {
                var devEventType = eventKind switch
                {
                    pa_subscription_event_type_t.New    => AudioDeviceEventType.Added,
                    pa_subscription_event_type_t.Remove => AudioDeviceEventType.Removed,
                    _                                   => AudioDeviceEventType.Changed
                };
                var args = new AudioDeviceEventArgs
                {
                    DeviceName = idStr,
                    EventType = devEventType
                };
                ThreadPool.QueueUserWorkItem(_ => DeviceChanged?.Invoke(this, args));
                break;
            }
        }
    }

    // ── Process resolution ───────────────────────────────────────────

    /// <summary>
    /// Resolves the actual executable name from /proc/&lt;pid&gt;/comm (then /proc/&lt;pid&gt;/exe).
    /// Returns just the filename (e.g. "firefox"), not the full path.
    /// </summary>
    private static string? ResolveProcessBinary(int? pid)
    {
        if (pid is null) return null;
        try
        {
            var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            if (!string.IsNullOrEmpty(comm))
                return comm;
        }
        catch { /* process may have exited, or permission denied */ }

        try
        {
            var target = Path.GetFileName(
                File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName ?? "");
            if (!string.IsNullOrEmpty(target))
                return target;
        }
        catch { /* process may have exited, or permission denied */ }

        return null;
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;

        if (_mainloop != IntPtr.Zero)
        {
            pa_threaded_mainloop_stop(_mainloop);
        }

        if (_context != IntPtr.Zero)
        {
            pa_context_disconnect(_context);
            pa_context_unref(_context);
            _context = IntPtr.Zero;
        }

        if (_mainloop != IntPtr.Zero)
        {
            pa_threaded_mainloop_free(_mainloop);
            _mainloop = IntPtr.Zero;
        }

        // Release delegate references.
        _stateCallback = null;
        _subscribeCallback = null;
    }
}
