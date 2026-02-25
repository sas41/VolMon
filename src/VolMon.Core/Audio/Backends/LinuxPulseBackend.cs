using System.Runtime.InteropServices;
using static VolMon.Core.Audio.Backends.LibPulse;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Linux audio backend using libpulse P/Invoke (works with both PulseAudio and PipeWire).
/// Maintains a persistent connection via <c>pa_mainloop</c> (non-threaded) driven by a
/// managed background thread, and communicates over PulseAudio's native Unix-socket
/// protocol — no process spawning or text parsing.
/// </summary>
/// <remarks>
/// Threading model:
/// <list type="bullet">
///   <item>A managed <see cref="Thread"/> pumps the <c>pa_mainloop</c> event loop.
///         Using a managed thread instead of <c>pa_threaded_mainloop</c> avoids native
///         thread creation that confuses the vsdbg debugger (which tracks native
///         clone/fork calls via ptrace and may lose the process).</item>
///   <item>All PA API calls from external threads must be wrapped in
///         <c>PaLock()</c> / <c>PaUnlock()</c>. <c>PaLock</c> acquires the monitor
///         and wakes the mainloop so it releases the lock.</item>
///   <item>The mainloop thread holds the same monitor while inside
///         <c>pa_mainloop_iterate</c>. After iterate returns, it calls
///         <c>Monitor.PulseAll</c> to wake any <c>PaWait</c> callers, then
///         releases the monitor briefly so external callers can acquire it.</item>
///   <item>Callbacks from PA fire on the mainloop thread <b>inside the lock</b> —
///         they must never call <c>PaLock</c> and must not block.</item>
///   <item>Subscription callbacks dispatch events to the thread pool to avoid
///         re-entrant deadlocks when event handlers call back into the backend.</item>
/// </list>
/// </remarks>
public sealed class LinuxPulseBackend : IAudioBackend
{
    private IntPtr _nativeMainloop;  // pa_mainloop* (non-threaded)
    private IntPtr _api;
    private IntPtr _context;
    private volatile bool _ready;
    private volatile bool _disposed;

    // Managed thread that pumps the pa_mainloop event loop.
    private Thread? _mainloopThread;
    private volatile bool _mainloopRunning;

    // Monitor used for mutual exclusion AND signalling between the mainloop
    // thread and external callers. Replaces both pa_threaded_mainloop_lock/unlock
    // and pa_threaded_mainloop_wait/signal.
    private readonly object _paLock = new();

    // Prevent GC collection of delegates passed to native code.
    private pa_context_notify_cb_t? _stateCallback;
    private pa_context_subscribe_cb_t? _subscribeCallback;

    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;
    public event EventHandler<DefaultSinkChangedEventArgs>? DefaultSinkChanged;

    /// <summary>
    /// Last known default sink name, used to detect changes when a Server
    /// subscription event fires.
    /// </summary>
    private string? _lastDefaultSinkName;

    // ── Connection lifecycle ─────────────────────────────────────────

    /// <summary>
    /// Connects to PulseAudio (or PipeWire-pulse) using the default server socket.
    /// Blocks until the context reaches READY or fails.
    /// </summary>
    private void EnsureConnected()
    {
        if (_ready) return;

        // Prevent any libpulse code path from calling fork(), which causes debuggers
        // (ptrace) to follow the child process and report a spurious "exit code 0".
        //
        // IMPORTANT: Environment.SetEnvironmentVariable only updates .NET's internal
        // dictionary — native code (libpulse) calls getenv() which reads the C runtime
        // environ, so we must use the POSIX setenv() via P/Invoke.
        setenv("PULSE_NO_SPAWN", "1", 1);

        _nativeMainloop = pa_mainloop_new();
        if (_nativeMainloop == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create pa_mainloop.");

        _api = pa_mainloop_get_api(_nativeMainloop);

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

        var flags = pa_context_flags_t.NoAutospawn;
        if (pa_context_connect(_context, null, flags, IntPtr.Zero) < 0)
            throw new InvalidOperationException(
                $"pa_context_connect failed: {GetError()}");

        // Poll the mainloop synchronously until the context reaches Ready.
        // No background thread yet — we own the mainloop exclusively here.
        while (true)
        {
            if (pa_mainloop_iterate(_nativeMainloop, 1, IntPtr.Zero) < 0)
                throw new InvalidOperationException("pa_mainloop_iterate failed");

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
        }

        // Start the managed background thread to pump the mainloop.
        _mainloopThread = new Thread(MainloopThreadProc)
        {
            Name = "pa-mainloop",
            IsBackground = true
        };
        _mainloopRunning = true;
        _mainloopThread.Start();

        // Clean up any stale VolMon modules left by a previous daemon run
        // that crashed or was killed without running Dispose().
        CleanUpStaleModules();
    }

    /// <summary>
    /// Scans the loaded PA module list and unloads any legacy <c>module-null-sink</c>
    /// or <c>module-loopback</c> instances whose argument strings reference a
    /// <c>volmon_compat_</c> sink name — these were created by older daemon builds
    /// that used the libpulse module approach and may have been left behind if
    /// the daemon was killed without running Dispose().
    /// </summary>
    private void CleanUpStaleModules()
    {
        var staleIndices = new List<uint>();
        var done = false;

        pa_module_info_cb_t cb = (_, infoPtr, eol, _) =>
        {
            if (eol > 0)
            {
                done = true;
                Monitor.PulseAll(_paLock);
                return;
            }
            if (infoPtr == IntPtr.Zero) return;

            var info = Marshal.PtrToStructure<pa_module_info>(infoPtr);
            var name = PtrToStringUtf8(info.name) ?? "";
            var args = PtrToStringUtf8(info.argument) ?? "";

            if ((name == "module-null-sink" || name == "module-loopback")
                && args.Contains(VirtualSinkPrefix, StringComparison.Ordinal))
            {
                staleIndices.Add(info.index);
            }
        };

        PaLock();
        try
        {
            var op = pa_context_get_module_info_list(_context, cb, IntPtr.Zero);
            if (op == IntPtr.Zero) return; // non-fatal — best effort

            while (!done)
                PaWait();

            pa_operation_unref(op);

            foreach (var idx in staleIndices)
            {
                var unloadOp = pa_context_unload_module(_context, idx, null, IntPtr.Zero);
                if (unloadOp != IntPtr.Zero) pa_operation_unref(unloadOp);
            }
        }
        finally
        {
            PaUnlock();
        }

        GC.KeepAlive(cb);
    }

    /// <summary>
    /// Background thread that continuously pumps the PA mainloop.
    /// Holds <c>_paLock</c> while iterating; after each iteration it pulses
    /// any threads waiting via <see cref="PaWait"/> and briefly yields the lock
    /// so external callers can issue operations.
    /// </summary>
    private void MainloopThreadProc()
    {
        lock (_paLock)
        {
            while (_mainloopRunning)
            {
                if (_nativeMainloop == IntPtr.Zero)
                    break;

                // Release the lock, let pa_mainloop_iterate block on poll(),
                // then re-acquire. We use a non-blocking iterate (block=0)
                // combined with Monitor.Wait to properly release the lock
                // while waiting for I/O.
                //
                // Actually, we need to release the lock during the blocking poll.
                // Monitor.Wait atomically releases and re-acquires — but we need
                // to call pa_mainloop_iterate, not just wait.
                //
                // Strategy: use non-blocking iterate, then Wait with a short timeout
                // to yield the lock for external callers.

                // Non-blocking iterate: process any pending events
                int ret = pa_mainloop_iterate(_nativeMainloop, 0, IntPtr.Zero);
                if (ret < 0)
                    break;

                if (ret > 0)
                {
                    // Events were dispatched — signal any waiters
                    Monitor.PulseAll(_paLock);
                }

                // Yield the lock briefly so external callers can issue operations.
                // The 5ms timeout keeps the mainloop responsive while allowing
                // other threads to acquire _paLock.
                Monitor.Wait(_paLock, 5);
            }
        }
    }

    /// <summary>
    /// State callback — runs on the mainloop thread.
    /// During the initial synchronous connect, the lock is NOT held (the polling
    /// loop checks state directly after each iterate). Once the background thread
    /// is running, the lock IS held and we signal waiting callers.
    /// </summary>
    private void OnContextState(IntPtr context, IntPtr userdata)
    {
        if (Monitor.IsEntered(_paLock))
            Monitor.PulseAll(_paLock);
    }

    private string GetError()
    {
        if (_context == IntPtr.Zero) return "no context";
        var errno = pa_context_errno(_context);
        var ptr = pa_strerror(errno);
        return PtrToStringUtf8(ptr) ?? $"error {errno}";
    }

    /// <summary>
    /// Acquires the PA lock. Must be paired with <see cref="PaUnlock"/> in a finally block.
    /// Wakes the mainloop so its <c>Monitor.Wait</c> returns and it can yield the lock.
    /// </summary>
    private void PaLock()
    {
        Monitor.Enter(_paLock);
    }

    private void PaUnlock()
    {
        // Pulse the mainloop thread so it resumes iterating
        Monitor.PulseAll(_paLock);
        Monitor.Exit(_paLock);
    }

    /// <summary>
    /// Waits for a signal from a PA callback. Atomically releases the lock and waits,
    /// then re-acquires the lock when signalled — same semantics as
    /// <c>pa_threaded_mainloop_wait</c>.
    /// </summary>
    private void PaWait()
    {
        Monitor.Wait(_paLock);
    }

    // ── Streams ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var streams = new List<AudioStream>();

        pa_sink_input_info_cb_t cb = (_, infoPtr, eol, _) =>
        {
            if (eol > 0)
            {
                Monitor.PulseAll(_paLock);
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

            // PipeWire sets "node.dont-reconnect" on streams that refuse sink moves
            // (e.g. WebRTC duplex streams with a hard-coded target device).
            var dontReconnect = proplist != IntPtr.Zero
                ? PtrToStringUtf8(pa_proplist_gets(proplist, "node.dont-reconnect"))
                : null;
            var isPinned = string.Equals(dontReconnect, "true", StringComparison.OrdinalIgnoreCase);

            streams.Add(new AudioStream
            {
                Id = info.index.ToString(),
                BinaryName = binaryName,
                ApplicationClass = appName,
                Volume = CvolumeToPercent(ref info.volume),
                Muted = info.mute != 0,
                ProcessId = pid,
                IsPinned = isPinned
            });
        };

        PaLock();
        try
        {
            var op = pa_context_get_sink_input_info_list(_context, cb, IntPtr.Zero);
            if (op == IntPtr.Zero)
                throw new InvalidOperationException($"pa_context_get_sink_input_info_list failed: {GetError()}");

            while (pa_operation_get_state(op) == pa_operation_state_t.Running)
                PaWait();

            pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

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

        PaLock();
        try
        {
            var op = pa_context_set_sink_input_volume(_context, idx, ref cvol, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default)
    {
        EnsureConnected();
        if (!uint.TryParse(streamId, out var idx))
            throw new ArgumentException($"Invalid stream ID: {streamId}", nameof(streamId));

        PaLock();
        try
        {
            var op = pa_context_set_sink_input_mute(_context, idx, muted ? 1 : 0, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
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
                    Monitor.PulseAll(_paLock);
                return;
            }
            if (infoPtr == IntPtr.Zero) return;

            var info = Marshal.PtrToStructure<pa_sink_info>(infoPtr);
            var sinkName = PtrToStringUtf8(info.name) ?? "";

            // Skip VolMon's own virtual null-sinks — they are internal to
            // CompatibilityMode and should not appear as controllable devices.
            if (sinkName.StartsWith(VirtualSinkPrefix, StringComparison.Ordinal)) return;

            devices.Add(new AudioDevice
            {
                Id = info.index.ToString(),
                Name = sinkName,
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
                    Monitor.PulseAll(_paLock);
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

        PaLock();
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

            while (pa_operation_get_state(op1) == pa_operation_state_t.Running ||
                   pa_operation_get_state(op2) == pa_operation_state_t.Running)
            {
                PaWait();
            }

            pa_operation_unref(op1);
            pa_operation_unref(op2);
        }
        finally
        {
            PaUnlock();
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

        PaLock();
        try
        {
            var op1 = pa_context_set_sink_volume_by_name(_context, deviceName, ref cvol, null, IntPtr.Zero);
            if (op1 != IntPtr.Zero) pa_operation_unref(op1);

            var op2 = pa_context_set_source_volume_by_name(_context, deviceName, ref cvol, null, IntPtr.Zero);
            if (op2 != IntPtr.Zero) pa_operation_unref(op2);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default)
    {
        EnsureConnected();
        var m = muted ? 1 : 0;

        PaLock();
        try
        {
            var op1 = pa_context_set_sink_mute_by_name(_context, deviceName, m, null, IntPtr.Zero);
            if (op1 != IntPtr.Zero) pa_operation_unref(op1);

            var op2 = pa_context_set_source_mute_by_name(_context, deviceName, m, null, IntPtr.Zero);
            if (op2 != IntPtr.Zero) pa_operation_unref(op2);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    // ── Monitoring ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task StartMonitoringAsync(CancellationToken ct = default)
    {
        EnsureConnected();

        // Seed the last known default sink name so that the first Server
        // subscription event can detect an actual change.
        _lastDefaultSinkName = await GetDefaultSinkNameAsync(ct);

        _subscribeCallback = OnSubscriptionEvent;

        PaLock();
        try
        {
            pa_context_set_subscribe_callback(_context, _subscribeCallback, IntPtr.Zero);

            var mask = pa_subscription_mask_t.Sink
                     | pa_subscription_mask_t.Source
                     | pa_subscription_mask_t.SinkInput
                     | pa_subscription_mask_t.SourceOutput
                     | pa_subscription_mask_t.Server;

            var op = pa_context_subscribe(_context, mask, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }
    }

    /// <inheritdoc/>
    public Task StopMonitoringAsync()
    {
        if (_context != IntPtr.Zero && _ready)
        {
            PaLock();
            try
            {
                pa_context_set_subscribe_callback(_context, null, IntPtr.Zero);
                var op = pa_context_subscribe(_context, pa_subscription_mask_t.Null, null, IntPtr.Zero);
                if (op != IntPtr.Zero) pa_operation_unref(op);
            }
            finally
            {
                PaUnlock();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscription callback — runs on the mainloop thread (lock already held).
    /// Dispatches events to the thread pool so that event handlers can safely call
    /// back into the backend without causing a re-entrant deadlock.
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

            case pa_subscription_event_type_t.Server:
            {
                // Server event fires when the default sink/source changes.
                // Query the new default sink name and fire DefaultSinkChanged
                // if it actually changed.
                ThreadPool.QueueUserWorkItem(_ => CheckDefaultSinkChanged());
                break;
            }
        }
    }

    /// <summary>
    /// Queries the current default sink name from the PA server and fires
    /// <see cref="DefaultSinkChanged"/> if it differs from the last known value.
    /// Called on the thread pool when a Server subscription event fires.
    /// </summary>
    private void CheckDefaultSinkChanged()
    {
        try
        {
            string? newDefault = null;
            var done = false;

            pa_server_info_cb_t cb = (_, infoPtr, _) =>
            {
                if (infoPtr != IntPtr.Zero)
                {
                    var info = Marshal.PtrToStructure<pa_server_info>(infoPtr);
                    newDefault = PtrToStringUtf8(info.default_sink_name);
                }
                done = true;
                Monitor.PulseAll(_paLock);
            };

            PaLock();
            try
            {
                var op = pa_context_get_server_info(_context, cb, IntPtr.Zero);
                if (op == IntPtr.Zero) return;

                while (!done)
                    PaWait();

                pa_operation_unref(op);
            }
            finally
            {
                PaUnlock();
            }

            GC.KeepAlive(cb);

            if (newDefault is not null && newDefault != _lastDefaultSinkName)
            {
                _lastDefaultSinkName = newDefault;
                DefaultSinkChanged?.Invoke(this,
                    new DefaultSinkChangedEventArgs { SinkName = newDefault });
            }
        }
        catch (Exception)
        {
            // Non-fatal — best effort detection of default sink changes.
        }
    }

    // ── Processes ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default)
    {
        var streams = await GetStreamsAsync(ct).ConfigureAwait(false);

        // Index streams by PID for fast join.
        var streamsByPid = new Dictionary<int, List<AudioStream>>();
        foreach (var stream in streams)
        {
            if (stream.ProcessId is { } pid)
            {
                if (!streamsByPid.TryGetValue(pid, out var list))
                    streamsByPid[pid] = list = [];
                list.Add(stream);
            }
        }

        // Enumerate every running process from /proc and build the result list.
        var result = new List<AudioProcess>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var dirName = Path.GetFileName(dir);
            if (!int.TryParse(dirName, out var pid)) continue;

            var name = ResolveProcessBinary(pid);
            if (name is null) continue; // process already exited

            streamsByPid.TryGetValue(pid, out var procStreams);

            result.Add(new AudioProcess
            {
                Id      = pid,
                Name    = name,
                Streams = procStreams ?? []
            });
        }

        return result;
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

    // ── CompatibilityMode: virtual sink management ───────────────────

    // Prefix used for all VolMon-created virtual filter sinks.
    // Must match the prefix used by StreamWatcher.VirtualSinkNameFor().
    private const string VirtualSinkPrefix = "volmon_compat_";

    // Maps sink node-name → PipeWireVirtualSink instance so we can
    // destroy it on demand.  Keyed by the node name string because
    // IAudioBackend.CreateVirtualSinkAsync returns a uint? "module index"
    // which we re-purpose here as a stable integer handle (the GC hash code
    // of the object, kept in _virtualSinkByHandle).
    private readonly Dictionary<string, PipeWireVirtualSink> _virtualSinks = new();
    private readonly Dictionary<uint, PipeWireVirtualSink>   _virtualSinkByHandle = new();

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a PipeWire <em>filter node</em> that acts as a virtual audio sink.
    /// Because the node has <c>media.category=Filter</c> it is invisible to
    /// GNOME's sound settings panel and pavucontrol's output device list —
    /// it does not appear as a hardware sink to the user.
    ///
    /// PipeWire still exposes the filter as a PulseAudio-compatible sink via
    /// the PipeWire-pulse compatibility layer, so
    /// <c>pa_context_move_sink_input_by_name</c> routes streams into it normally.
    ///
    /// The returned <c>uint</c> is an opaque handle used as the argument to
    /// <see cref="DestroyVirtualSinkAsync"/>. It has no meaning outside this class.
    /// </remarks>
    public async Task<uint?> CreateVirtualSinkAsync(string sinkName, string description,
        CancellationToken ct = default)
    {
        // Query the current default sink so the virtual sink links to the right
        // hardware output from the start, instead of guessing from the registry.
        var defaultSink = await GetDefaultSinkNameAsync(ct);

        PipeWireVirtualSink sink;
        try
        {
            sink = PipeWireVirtualSink.Create(sinkName, description, defaultSink);
        }
        catch (Exception ex)
        {
            // Log and return null to signal failure to the caller (StreamWatcher).
            System.Diagnostics.Debug.WriteLine(
                $"[VolMon] PipeWireVirtualSink.Create({sinkName}) failed: {ex.Message}");
            return null;
        }

        // Use the object's identity hash as a stable uint handle.
        uint handle = (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sink);

        _virtualSinks[sinkName] = sink;
        _virtualSinkByHandle[handle] = sink;

        return handle;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Destroys the <see cref="PipeWireVirtualSink"/> identified by the handle
    /// returned from <see cref="CreateVirtualSinkAsync"/>, stopping its thread loop
    /// and disconnecting the filter node from the PipeWire graph.
    /// </remarks>
    public Task DestroyVirtualSinkAsync(uint moduleIndex, CancellationToken ct = default)
    {
        if (_virtualSinkByHandle.TryGetValue(moduleIndex, out var sink))
        {
            _virtualSinkByHandle.Remove(moduleIndex);
            _virtualSinks.Remove(sink.NodeName);
            sink.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MoveStreamToSinkAsync(string streamId, string sinkName,
        CancellationToken ct = default)
    {
        EnsureConnected();
        if (!uint.TryParse(streamId, out var idx))
            throw new ArgumentException($"Invalid stream ID: {streamId}", nameof(streamId));

        PaLock();
        try
        {
            var op = pa_context_move_sink_input_by_name(
                _context, idx, sinkName, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets the virtual sink volume via the PulseAudio API
    /// (<c>pa_context_set_sink_volume_by_name</c>) rather than the PipeWire native
    /// <c>pw_node_set_param</c> API. The PA-compat layer correctly translates
    /// <c>pa_cvolume</c> to PipeWire's <c>channelVolumes</c> property, which is the
    /// property that determines the user-visible sink volume. The PipeWire native
    /// <c>SPA_PROP_volume</c> is a separate master multiplier that does NOT affect
    /// the PA-reported volume.
    /// </remarks>
    public Task SetVirtualSinkVolumeAsync(string sinkName, int volume,
        CancellationToken ct = default)
    {
        EnsureConnected();
        var cvol = PercentToCvolume(2, volume);

        PaLock();
        try
        {
            var op = pa_context_set_sink_volume_by_name(
                _context, sinkName, ref cvol, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sets the virtual sink mute state via the PulseAudio API
    /// (<c>pa_context_set_sink_mute_by_name</c>) for the same reasons as
    /// <see cref="SetVirtualSinkVolumeAsync"/>.
    /// </remarks>
    public Task SetVirtualSinkMuteAsync(string sinkName, bool muted,
        CancellationToken ct = default)
    {
        EnsureConnected();

        PaLock();
        try
        {
            var op = pa_context_set_sink_mute_by_name(
                _context, sinkName, muted ? 1 : 0, null, IntPtr.Zero);
            if (op != IntPtr.Zero) pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

        return Task.CompletedTask;
    }

    // ── Default sink / re-linking ────────────────────────────────────

    /// <inheritdoc/>
    public Task<string?> GetDefaultSinkNameAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        string? result = null;
        var done = false;

        pa_server_info_cb_t cb = (_, infoPtr, _) =>
        {
            if (infoPtr != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<pa_server_info>(infoPtr);
                result = PtrToStringUtf8(info.default_sink_name);
            }
            done = true;
            Monitor.PulseAll(_paLock);
        };

        PaLock();
        try
        {
            var op = pa_context_get_server_info(_context, cb, IntPtr.Zero);
            if (op == IntPtr.Zero) return Task.FromResult<string?>(null);

            while (!done)
                PaWait();

            pa_operation_unref(op);
        }
        finally
        {
            PaUnlock();
        }

        GC.KeepAlive(cb);
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task RelinkVirtualSinkAsync(string virtualSinkName, string targetSinkName,
        CancellationToken ct = default)
    {
        if (_virtualSinks.TryGetValue(virtualSinkName, out var sink))
            sink.RelinkToSink(targetSinkName);

        return Task.CompletedTask;
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;

        // Tear down all remaining virtual sinks first (each owns a pw_thread_loop).
        foreach (var sink in _virtualSinks.Values)
            sink.Dispose();
        _virtualSinks.Clear();
        _virtualSinkByHandle.Clear();

        // Stop the managed mainloop thread.
        _mainloopRunning = false;
        if (_nativeMainloop != IntPtr.Zero)
            pa_mainloop_wakeup(_nativeMainloop);

        // Pulse the lock so the mainloop thread wakes from Monitor.Wait
        lock (_paLock) { Monitor.PulseAll(_paLock); }
        _mainloopThread?.Join(TimeSpan.FromSeconds(2));

        if (_context != IntPtr.Zero)
        {
            pa_context_disconnect(_context);
            pa_context_unref(_context);
            _context = IntPtr.Zero;
        }

        if (_nativeMainloop != IntPtr.Zero)
        {
            pa_mainloop_free(_nativeMainloop);
            _nativeMainloop = IntPtr.Zero;
        }

        // Release delegate references.
        _stateCallback = null;
        _subscribeCallback = null;
    }
}
