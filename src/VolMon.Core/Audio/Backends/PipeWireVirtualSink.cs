using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static VolMon.Core.Audio.Backends.LibPipeWire;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Creates and owns a PipeWire virtual audio sink for CompatibilityMode routing.
///
/// <para><b>What it creates</b></para>
/// A <c>support.null-audio-sink</c> adapter node with:
/// <list type="bullet">
///   <item><c>media.class = Audio/Sink</c> — PA compat layer exposes it as a real sink,
///         so <c>pa_context_move_sink_input_by_name</c> and volume control work.</item>
///   <item><c>device.class = filter</c> — WirePlumber hides it from GNOME Sound Settings
///         and from pavucontrol's Playback Devices list.</item>
///   <item><c>node.passive = true</c> — doesn't keep the audio graph alive when idle.</item>
/// </list>
///
/// <para><b>Audio routing</b></para>
/// WirePlumber does NOT auto-link the null-sink's monitor output ports to the hardware sink.
/// After creating the node we poll for its monitor ports (FL/FR) to appear in the registry,
/// then explicitly create <c>link-factory</c> link objects connecting them to the default
/// hardware sink's playback input ports. Without these links, audio would be silently discarded.
///
/// <para><b>Stale node cleanup</b></para>
/// Nodes created with <c>object.linger=1</c> survive across client disconnects.  Our nodes
/// do NOT set linger, but a previous daemon crash can leave orphaned nodes. At startup we
/// enumerate the registry and call <c>pw_registry_destroy</c> on any existing
/// <c>volmon_compat_*</c> Audio/Sink nodes before creating the new one.
///
/// <para><b>Threading model</b></para>
/// Uses a non-threaded <c>pw_main_loop</c> driven by a managed C# <see cref="Thread"/>,
/// identical to the PA backend's approach. This avoids any native <c>pthread_create</c>
/// (which the vsdbg/ptrace debugger intercepts as a fork and kills the process).
///
/// The managed thread holds <c>_pwLock</c> while calling <c>pw_loop_iterate</c> and
/// briefly yields it each cycle via <c>Monitor.Wait(_pwLock, 5ms)</c>, allowing the
/// initialisation code running on the caller thread to safely call PipeWire API methods
/// under the same lock and then <see cref="PwWait"/> for sync replies.
/// </summary>
internal sealed class PipeWireVirtualSink : IDisposable
{
    // ── Constants ────────────────────────────────────────────────────

    /// <summary>Number of monitor+playback ports expected on the new null-audio-sink (FL+FR each side).</summary>
    private const int ExpectedPortCount = 4;

    /// <summary>Milliseconds to poll for ports to appear after node creation.</summary>
    private const int PortPollTimeoutMs = 3_000;

    // ── Native handles ───────────────────────────────────────────────

    private IntPtr _mainLoop      = IntPtr.Zero;  // pw_main_loop*
    private IntPtr _pwLoop        = IntPtr.Zero;  // pw_loop* (owned by _mainLoop)
    private IntPtr _context       = IntPtr.Zero;  // pw_context*
    private IntPtr _core          = IntPtr.Zero;  // pw_core*
    private IntPtr _registry      = IntPtr.Zero;  // pw_registry*
    private IntPtr _nodeProxy     = IntPtr.Zero;  // creation proxy (from pw_core_create_object)

    // ── Managed loop thread ──────────────────────────────────────────

    private Thread?       _loopThread;
    private volatile bool _loopRunning;

    // Monitor for mutual exclusion between the loop thread and external callers.
    private readonly object _pwLock = new();

    // ── Sync / registry state ────────────────────────────────────────

    // Set to true by the core 'done' callback when our sync seq arrives.
    private volatile bool _syncDone;
    private          int  _syncSeq;

    // Live registry snapshot — written from the loop thread callbacks, read
    // from Initialize() under _pwLock (safe because Initialize() always holds
    // the lock or waits via PwWait which re-acquires it).
    private readonly List<NodeInfo> _nodes = new();
    private readonly List<PortInfo> _ports = new();

    // ── Pinned GC handles for native callback structs ────────────────
    // These must stay alive for as long as the listeners are registered.

    private GCHandle _coreEventsHandle;
    private GCHandle _regEventsHandle;
    private GCHandle _coreHookHandle;
    private GCHandle _regHookHandle;

    // Delegates must be kept alive to prevent GC from collecting them while
    // native code still holds function pointers into them.
    private PwCoreDoneCallback?              _doneCb;
    private PwRegistryGlobalCallback?        _globalCb;
    private PwRegistryGlobalRemoveCallback?  _globalRemoveCb;

    // ── State ────────────────────────────────────────────────────────

    private volatile bool _disposed;

    /// <summary>
    /// The <c>node.name</c> of the created sink.
    /// Pass this to <c>pa_context_move_sink_input_by_name</c> to route streams here,
    /// and to <c>pa_context_set_sink_volume_by_name</c> for volume control.
    /// </summary>
    public string NodeName { get; }

    // ── Inner types ──────────────────────────────────────────────────

    private record struct NodeInfo(uint Id, string Name, string MediaClass);
    private record struct PortInfo(uint Id, uint NodeId, string PortName, string Direction, bool IsMonitor);

    // ── Factory ──────────────────────────────────────────────────────

    private PipeWireVirtualSink(string nodeName) => NodeName = nodeName;

    /// <summary>
    /// Creates a virtual sink node in PipeWire and starts the background event loop.
    /// Blocks until the sink node is created and linked to the default hardware output.
    /// </summary>
    /// <param name="nodeName">Stable unique name, e.g. <c>volmon_compat_abc123</c>.</param>
    /// <param name="description">Human-readable description (not shown in GNOME device picker).</param>
    public static PipeWireVirtualSink Create(string nodeName, string description)
    {
        var sink = new PipeWireVirtualSink(nodeName);
        sink.Initialize(description);
        return sink;
    }

    // ── Initialization ───────────────────────────────────────────────

    private void Initialize(string description)
    {
        pw_init(IntPtr.Zero, IntPtr.Zero);

        // 1. Non-threaded main loop ──────────────────────────────────
        _mainLoop = pw_main_loop_new(IntPtr.Zero);
        if (_mainLoop == IntPtr.Zero)
            throw new InvalidOperationException($"pw_main_loop_new failed for {NodeName}");

        _pwLoop = pw_main_loop_get_loop(_mainLoop);
        if (_pwLoop == IntPtr.Zero)
            throw new InvalidOperationException("pw_main_loop_get_loop returned NULL");

        // 2. Context ─────────────────────────────────────────────────
        _context = pw_context_new(_pwLoop, IntPtr.Zero, UIntPtr.Zero);
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException($"pw_context_new failed for {NodeName}");

        // 3. Start the loop thread BEFORE connecting ─────────────────
        // pw_context_connect sends protocol messages that need the loop
        // to iterate in order to complete the connection handshake.
        _loopRunning = true;
        _loopThread = new Thread(LoopThreadProc)
        {
            Name         = $"pw-vsink-{NodeName}",
            IsBackground = true
        };
        _loopThread.Start();

        // All subsequent API calls happen under _pwLock so that they
        // interleave safely with LoopThreadProc.
        lock (_pwLock)
        {
            // 4. Connect to PipeWire daemon ───────────────────────────
            _core = pw_context_connect(_context, IntPtr.Zero, UIntPtr.Zero);
            if (_core == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"pw_context_connect failed for {NodeName} (errno {Marshal.GetLastWin32Error()})");

            // 5. Set up core listener (for sync 'done' callback) ──────
            SetupCoreListener();

            // 6. Set up registry listener (for global object events) ──
            _registry = pw_core_get_registry(_core, PW_VERSION_REGISTRY, UIntPtr.Zero);
            if (_registry == IntPtr.Zero)
                throw new InvalidOperationException("pw_core_get_registry returned NULL");
            SetupRegistryListener();

            // 7. Initial sync — enumerate existing graph ──────────────
            PwSync();

            // 8. Destroy any stale volmon_compat_* Audio/Sink nodes ───
            var stale = _nodes
                .Where(n => n.Name.StartsWith("volmon_compat_", StringComparison.Ordinal)
                         && n.MediaClass == "Audio/Sink")
                .ToList();
            foreach (var node in stale)
                pw_registry_destroy(_registry, node.Id);

            if (stale.Count > 0)
                PwSync();  // let destroy requests propagate

            // 9. Create the virtual sink node ─────────────────────────
            var props = pw_properties_new_string(string.Empty);
            if (props == IntPtr.Zero)
                throw new InvalidOperationException("pw_properties_new_string failed");

            pw_properties_set(props, "factory.name",           "support.null-audio-sink");
            pw_properties_set(props, PW_KEY_MEDIA_CLASS,       "Audio/Sink");
            pw_properties_set(props, "device.class",           "filter");
            pw_properties_set(props, "node.passive",           "true");
            pw_properties_set(props, "monitor.channel-volumes","true");
            pw_properties_set(props, "monitor.passthrough",    "true");
            pw_properties_set(props, PW_KEY_NODE_NAME,         NodeName);
            pw_properties_set(props, PW_KEY_NODE_DESCRIPTION,  description);
            pw_properties_set(props, "audio.channels",         "2");
            pw_properties_set(props, "audio.position",         "[ FL, FR ]");

            // pw_properties* is castable to spa_dict* — the struct's first field IS spa_dict.
            _nodeProxy = pw_core_create_object(
                _core, "adapter", PW_TYPE_INTERFACE_Node, PW_VERSION_NODE,
                props, UIntPtr.Zero);

            pw_properties_free(props);

            if (_nodeProxy == IntPtr.Zero)
                throw new InvalidOperationException($"pw_core_create_object failed for {NodeName}");

            // 10. Wait for node to appear in the registry ─────────────
            PwSync();

            uint volNodeId = FindNodeId(NodeName);
            if (volNodeId == uint.MaxValue)
                throw new InvalidOperationException($"Node '{NodeName}' did not appear in registry after creation");

            // 11. Poll for ports to be registered (async via WirePlumber)
            // Ports are not created synchronously — they arrive later as
            // registry 'global' events.  Poll by iterating with short timeouts.
            PollUntilPorts(volNodeId, ExpectedPortCount, PortPollTimeoutMs);

            // 12. Find monitor output ports of our node ────────────────
            uint monitorFl = FindPort(volNodeId, direction: "out", nameContains: "FL", monitorOnly: true);
            uint monitorFr = FindPort(volNodeId, direction: "out", nameContains: "FR", monitorOnly: true);

            // 13. Find hardware sink and its playback input ports ──────
            uint hwNodeId = FindHardwareSinkId();
            if (hwNodeId == uint.MaxValue)
            {
                // No hardware sink found — sink is created but audio won't flow.
                // This is non-fatal; the sink is still addressable by PA.
                return;
            }

            uint hwFl = FindPort(hwNodeId, direction: "in", nameContains: "FL", monitorOnly: false);
            uint hwFr = FindPort(hwNodeId, direction: "in", nameContains: "FR", monitorOnly: false);

            // 14. Create PipeWire links: monitor out → hw sink in ──────
            if (monitorFl != uint.MaxValue && hwFl != uint.MaxValue)
                CreateLink(volNodeId, monitorFl, hwNodeId, hwFl);

            if (monitorFr != uint.MaxValue && hwFr != uint.MaxValue)
                CreateLink(volNodeId, monitorFr, hwNodeId, hwFr);

            if ((monitorFl != uint.MaxValue && hwFl != uint.MaxValue) ||
                (monitorFr != uint.MaxValue && hwFr != uint.MaxValue))
            {
                PwSync();  // ensure link creation requests are flushed
            }
        }
    }

    // ── Listener setup ───────────────────────────────────────────────

    private void SetupCoreListener()
    {
        _doneCb = OnCoreDone;

        var events = new PwCoreEvents
        {
            Version = PW_VERSION_CORE_EVENTS,
            Done    = Marshal.GetFunctionPointerForDelegate(_doneCb),
        };
        _coreEventsHandle = GCHandle.Alloc(events, GCHandleType.Pinned);

        var hook = new SpaHook();
        _coreHookHandle = GCHandle.Alloc(hook, GCHandleType.Pinned);

        pw_core_add_listener(
            _core,
            _coreHookHandle.AddrOfPinnedObject(),
            _coreEventsHandle.AddrOfPinnedObject(),
            IntPtr.Zero);
    }

    private void SetupRegistryListener()
    {
        _globalCb       = OnRegistryGlobal;
        _globalRemoveCb = OnRegistryGlobalRemove;

        var events = new PwRegistryEvents
        {
            Version      = PW_VERSION_REGISTRY_EVENTS,
            Global       = Marshal.GetFunctionPointerForDelegate(_globalCb),
            GlobalRemove = Marshal.GetFunctionPointerForDelegate(_globalRemoveCb),
        };
        _regEventsHandle = GCHandle.Alloc(events, GCHandleType.Pinned);

        var hook = new SpaHook();
        _regHookHandle = GCHandle.Alloc(hook, GCHandleType.Pinned);

        pw_registry_add_listener(
            _registry,
            _regHookHandle.AddrOfPinnedObject(),
            _regEventsHandle.AddrOfPinnedObject(),
            IntPtr.Zero);
    }

    // ── Registry callbacks (called from loop thread, under _pwLock) ──

    private void OnCoreDone(IntPtr data, uint id, int seq)
    {
        if (seq == _syncSeq)
        {
            _syncDone = true;
            Monitor.PulseAll(_pwLock);
        }
    }

    private void OnRegistryGlobal(IntPtr data, uint id, uint permissions,
        string type, uint version, IntPtr propsPtr)
    {
        if (type == PW_TYPE_INTERFACE_Node)
        {
            string? name        = SpaDictLookup(propsPtr, "node.name");
            string? mediaClass  = SpaDictLookup(propsPtr, "media.class");
            if (name != null && mediaClass != null)
                _nodes.Add(new NodeInfo(id, name, mediaClass));
        }
        else if (type == PW_TYPE_INTERFACE_Port)
        {
            string? dir     = SpaDictLookup(propsPtr, "port.direction");
            string? nodeId  = SpaDictLookup(propsPtr, "node.id");
            string? portName= SpaDictLookup(propsPtr, "port.name");
            string? monitor = SpaDictLookup(propsPtr, "port.monitor");
            if (dir != null && nodeId != null && portName != null
                && uint.TryParse(nodeId, out uint nid))
            {
                bool isMonitor = monitor == "true";
                _ports.Add(new PortInfo(id, nid, portName, dir, isMonitor));
            }
        }
    }

    private void OnRegistryGlobalRemove(IntPtr data, uint id)
    {
        _nodes.RemoveAll(n => n.Id == id);
        _ports.RemoveAll(p => p.Id == id);
    }

    // ── Helpers (must be called under _pwLock) ───────────────────────

    /// <summary>
    /// Sends a sync roundtrip and blocks (via Monitor.Wait) until the 'done' callback fires.
    /// Must be called under <c>_pwLock</c>.
    /// </summary>
    private void PwSync()
    {
        _syncDone = false;
        _syncSeq  = pw_core_sync(_core, PW_ID_CORE, 0);

        while (!_syncDone)
            Monitor.Wait(_pwLock, 100);
    }

    /// <summary>
    /// Polls (iterating the loop under _pwLock) until <paramref name="minPorts"/> ports appear
    /// for <paramref name="nodeId"/>, or <paramref name="timeoutMs"/> elapses.
    /// Must be called under <c>_pwLock</c>.
    /// </summary>
    private void PollUntilPorts(uint nodeId, int minPorts, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs && CountPorts(nodeId) < minPorts)
        {
            // Briefly release the lock so LoopThreadProc can call pw_loop_iterate.
            Monitor.Wait(_pwLock, 10);
            elapsed += 10;
        }
    }

    private int CountPorts(uint nodeId)
        => _ports.Count(p => p.NodeId == nodeId);

    private uint FindNodeId(string name)
    {
        var node = _nodes.FirstOrDefault(n => n.Name == name);
        // NodeInfo is a value type; Id == 0 means "not found" (PipeWire IDs start at 1).
        return node.Id != 0 ? node.Id : uint.MaxValue;
    }

    private uint FindHardwareSinkId()
    {
        var hw = _nodes.FirstOrDefault(n =>
            n.MediaClass == "Audio/Sink" &&
            n.Name.Contains("alsa", StringComparison.OrdinalIgnoreCase));
        return hw.Id != 0 ? hw.Id : uint.MaxValue;
    }

    private uint FindPort(uint nodeId, string direction, string nameContains, bool monitorOnly)
    {
        var port = _ports.FirstOrDefault(p =>
            p.NodeId == nodeId &&
            p.Direction == direction &&
            (!monitorOnly || p.IsMonitor) &&
            (monitorOnly || !p.IsMonitor) &&
            p.PortName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        return port.Id != 0 ? port.Id : uint.MaxValue;
    }

    private void CreateLink(uint outNode, uint outPort, uint inNode, uint inPort)
    {
        var props = pw_properties_new_string(string.Empty);
        if (props == IntPtr.Zero) return;

        pw_properties_set(props, "link.output.node", outNode.ToString());
        pw_properties_set(props, "link.output.port", outPort.ToString());
        pw_properties_set(props, "link.input.node",  inNode.ToString());
        pw_properties_set(props, "link.input.port",  inPort.ToString());

        pw_core_create_object(_core, "link-factory", PW_TYPE_INTERFACE_Link,
            PW_VERSION_LINK, props, UIntPtr.Zero);

        pw_properties_free(props);
    }

    // ── Managed loop thread ──────────────────────────────────────────

    private void LoopThreadProc()
    {
        lock (_pwLock)
        {
            while (_loopRunning)
            {
                if (_pwLoop == IntPtr.Zero) break;

                int dispatched = pw_loop_iterate(_pwLoop, 0);
                if (dispatched < 0) break;

                if (dispatched > 0)
                    Monitor.PulseAll(_pwLock);

                Monitor.Wait(_pwLock, 5);
            }
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopRunning = false;
        if (_mainLoop != IntPtr.Zero)
            pw_main_loop_quit(_mainLoop);

        lock (_pwLock) { Monitor.PulseAll(_pwLock); }
        _loopThread?.Join(TimeSpan.FromSeconds(2));

        // Tear down in reverse order: core → context → loop.
        // No lock needed — loop thread has stopped.
        if (_core != IntPtr.Zero)
        {
            pw_core_disconnect(_core);
            _core = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            pw_context_destroy(_context);
            _context = IntPtr.Zero;
        }

        if (_mainLoop != IntPtr.Zero)
        {
            pw_main_loop_destroy(_mainLoop);
            _mainLoop = IntPtr.Zero;
            _pwLoop   = IntPtr.Zero;
        }

        // Free GC handles for pinned native structs.
        if (_coreEventsHandle.IsAllocated) _coreEventsHandle.Free();
        if (_regEventsHandle.IsAllocated)  _regEventsHandle.Free();
        if (_coreHookHandle.IsAllocated)   _coreHookHandle.Free();
        if (_regHookHandle.IsAllocated)    _regHookHandle.Free();
    }
}
