using System.Runtime.InteropServices;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// P/Invoke bindings for libpipewire-0.3 (native PipeWire client library).
/// Used by <see cref="PipeWireVirtualSink"/> to create virtual audio sinks for
/// CompatibilityMode routing without appearing in GNOME's device picker or pavucontrol.
///
/// All functions marked "static inline" in the PipeWire headers are also exported
/// as real symbols from libpipewire-0.3.so, so P/Invoke works normally.
/// </summary>
internal static class LibPipeWire
{
    private const string Lib = "libpipewire-0.3.so";

    // ── Well-known IDs ───────────────────────────────────────────────

    /// <summary>PW_ID_CORE — default remote core ID after connect.</summary>
    public const uint PW_ID_CORE = 0;

    // ── Interface type strings ────────────────────────────────────────

    public const string PW_TYPE_INTERFACE_Node     = "PipeWire:Interface:Node";
    public const string PW_TYPE_INTERFACE_Link     = "PipeWire:Interface:Link";
    public const string PW_TYPE_INTERFACE_Registry = "PipeWire:Interface:Registry";
    public const string PW_TYPE_INTERFACE_Port     = "PipeWire:Interface:Port";

    // ── Interface versions ────────────────────────────────────────────

    public const uint PW_VERSION_NODE     = 3;
    public const uint PW_VERSION_LINK     = 3;
    public const uint PW_VERSION_REGISTRY = 3;

    // ── Event struct versions (embedded .version field) ───────────────

    /// <summary>PW_VERSION_REGISTRY_EVENTS — value for the version field of pw_registry_events.</summary>
    public const uint PW_VERSION_REGISTRY_EVENTS = 0;

    /// <summary>PW_VERSION_CORE_EVENTS — value for the version field of pw_core_events.</summary>
    public const uint PW_VERSION_CORE_EVENTS = 1;

    // ── PW_KEY_* string constants (from pipewire/keys.h) ─────────────

    public const string PW_KEY_MEDIA_TYPE        = "media.type";
    public const string PW_KEY_MEDIA_CATEGORY    = "media.category";
    public const string PW_KEY_MEDIA_ROLE        = "media.role";
    public const string PW_KEY_MEDIA_CLASS       = "media.class";
    public const string PW_KEY_FORMAT_DSP        = "format.dsp";
    public const string PW_KEY_PORT_NAME         = "port.name";
    public const string PW_KEY_NODE_NAME         = "node.name";
    public const string PW_KEY_NODE_NICK         = "node.nick";
    public const string PW_KEY_NODE_DESCRIPTION  = "node.description";

    // ── Lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the PipeWire library. Pass IntPtr.Zero for both args to ignore argc/argv.
    /// Idempotent — safe to call multiple times (reference-counted internally).
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_init")]
    public static extern void pw_init(IntPtr argc, IntPtr argv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_deinit")]
    public static extern void pw_deinit();

    // ── Non-threaded main loop (pw_main_loop) ────────────────────────
    //
    // We use pw_main_loop (non-threaded) driven from a managed C# Thread,
    // exactly mirroring the PA backend's pa_mainloop approach.  This avoids
    // pw_thread_loop_start() which calls pthread_create() — a native clone()
    // syscall that the vsdbg/ptrace debugger intercepts and misinterprets as a
    // fork, causing the process to exit with code 0 immediately.

    /// <summary>Creates a non-threaded main loop. Returns NULL on failure.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_main_loop_new")]
    public static extern IntPtr pw_main_loop_new(IntPtr props);  // const spa_dict* — pass Zero

    /// <summary>Destroys the main loop and frees all resources.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_main_loop_destroy")]
    public static extern void pw_main_loop_destroy(IntPtr loop);

    /// <summary>Returns the underlying <c>pw_loop*</c>. Valid for the lifetime of the main loop.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_main_loop_get_loop")]
    public static extern IntPtr pw_main_loop_get_loop(IntPtr loop);

    /// <summary>
    /// Signals the main loop to quit. Safe to call from any thread.
    /// Causes a blocked <see cref="pw_loop_iterate"/> to return.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_main_loop_quit")]
    public static extern int pw_main_loop_quit(IntPtr loop);

    // ── pw_loop iteration (non-threaded) ─────────────────────────────

    /// <summary>
    /// Runs a single iteration of the loop. When <paramref name="timeoutMs"/> is -1, blocks
    /// until an event is ready; when 0, returns immediately after dispatching pending events.
    /// Returns the number of sources dispatched, or negative on error.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_loop_iterate")]
    public static extern int pw_loop_iterate(IntPtr loop, int timeoutMs);

    // ── Properties ───────────────────────────────────────────────────

    /// <summary>
    /// Creates an empty <c>pw_properties</c> object. Pass a null/empty args string.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_properties_new_string")]
    public static extern IntPtr pw_properties_new_string(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    /// <summary>Sets a single key/value pair on an existing properties object.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_properties_set")]
    public static extern int pw_properties_set(IntPtr props,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? value);

    /// <summary>Frees a <c>pw_properties</c> object. Only call when ownership was NOT transferred.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_properties_free")]
    public static extern void pw_properties_free(IntPtr props);

    // ── Context and Core ─────────────────────────────────────────────

    /// <summary>
    /// Creates a new <c>pw_context</c>. Ownership of <paramref name="props"/> is taken.
    /// <paramref name="mainLoop"/> must be from <see cref="pw_main_loop_get_loop"/>.
    /// Returns NULL on failure.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_context_new")]
    public static extern IntPtr pw_context_new(
        IntPtr mainLoop,      // pw_loop*
        IntPtr props,         // pw_properties* — ownership taken, or NULL
        UIntPtr userDataSize);

    /// <summary>Destroys the context. Must be called after the core is disconnected.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_context_destroy")]
    public static extern void pw_context_destroy(IntPtr context);

    /// <summary>
    /// Connects to the default PipeWire daemon and returns a <c>pw_core*</c>.
    /// Ownership of <paramref name="props"/> is taken.
    /// Returns NULL on failure (check errno).
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_context_connect")]
    public static extern IntPtr pw_context_connect(
        IntPtr context,       // pw_context*
        IntPtr props,         // pw_properties* — ownership taken, or NULL
        UIntPtr userDataSize);

    /// <summary>Disconnects from the PipeWire daemon and frees the core proxy.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_core_disconnect")]
    public static extern int pw_core_disconnect(IntPtr core);

    /// <summary>
    /// Sends a sync roundtrip to the server and returns the sequence number.
    /// The server's reply fires the <c>done</c> event on the core listener.
    /// Used to wait for previously issued requests to complete.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_core_sync")]
    public static extern int pw_core_sync(IntPtr core, uint id, int seq);

    /// <summary>
    /// Binds to an existing global object in the registry and returns a typed proxy.
    /// For nodes use type <c>PW_TYPE_INTERFACE_Node</c>, version <c>PW_VERSION_NODE</c>.
    /// Returns the proxy pointer, or NULL on failure.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_registry_bind")]
    public static extern IntPtr pw_registry_bind(
        IntPtr registry,      // pw_registry*
        uint   id,            // global object ID
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        uint   version,
        UIntPtr userDataSize);

    /// <summary>
    /// Sets a parameter on a node proxy. <paramref name="podPtr"/> must point to a
    /// valid <c>spa_pod*</c> for the parameter.
    /// Use <see cref="SPA_PARAM_Props"/> with a Props object to set volume/mute.
    /// Returns 0 on success, negative errno on failure.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_node_set_param")]
    public static extern int pw_node_set_param(
        IntPtr node,          // pw_node*
        uint   paramId,       // SPA_PARAM_*
        uint   flags,
        IntPtr podPtr);       // const struct spa_pod*

    /// <summary>
    /// Destroys a proxy object returned by <see cref="pw_registry_bind"/> or
    /// <see cref="pw_core_create_object"/>.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_proxy_destroy")]
    public static extern void pw_proxy_destroy(IntPtr proxy);

    // ── SPA param / type constants ───────────────────────────────────
    //
    // Values verified by byte-dumping spa_pod_builder output in C on x86-64 Linux.

    /// <summary>SPA_TYPE_Object — pod header type tag for all object pods.</summary>
    public const uint SPA_TYPE_Object = 15;

    /// <summary>SPA_PARAM_Props — parameter ID for node properties (volume, mute, …).</summary>
    public const uint SPA_PARAM_Props = 2;

    /// <summary>
    /// SPA_TYPE_OBJECT_Props — object-type field inside the Props object body.
    /// = 0x00040002 = 262146.
    /// </summary>
    public const uint SPA_TYPE_OBJECT_Props = 262146;

    /// <summary>SPA_PROP_volume — key for the volume float in a Props object. = 0x00010003.</summary>
    public const uint SPA_PROP_volume = 65539;

    /// <summary>SPA_PROP_mute — key for the mute bool in a Props object. = 0x00010004.</summary>
    public const uint SPA_PROP_mute = 65540;

    /// <summary>SPA_PROP_channelVolumes — key for the per-channel volume array in a Props object. = 0x00010006.</summary>
    /// <remarks>
    /// This is the property that PipeWire's PulseAudio compatibility layer reads to
    /// report the sink volume via <c>pa_sink_info.volume</c>. The scalar
    /// <see cref="SPA_PROP_volume"/> is a separate master multiplier that does NOT
    /// affect the PA-reported volume — it is typically left at 1.0.
    /// Values use PulseAudio's cubic scale: <c>raw = (fraction)^3</c>.
    /// </remarks>
    public const uint SPA_PROP_channelVolumes = 65542;

    /// <summary>SPA_TYPE_Array — pod type tag for a homogeneous array of values.</summary>
    public const uint SPA_TYPE_Array = 14;

    /// <summary>SPA_TYPE_Float — pod value type tag for a 32-bit float.</summary>
    public const uint SPA_TYPE_Float = 6;

    /// <summary>SPA_TYPE_Bool — pod value type tag for a boolean (stored as uint32).</summary>
    public const uint SPA_TYPE_Bool = 2;

    // ── spa_pod builder (managed) ────────────────────────────────────
    //
    // spa_pod binary layout (little-endian, 8-byte aligned):
    //
    //   spa_pod header  (8 bytes):   size(u32) + type(u32)
    //   spa_pod_object body (8 bytes): objectType(u32) + id(u32)
    //   spa_pod_prop  (key(u32) + flags(u32) + value_pod):
    //     value_pod:  size(u32) + type(u32) + value_bytes + pad_to_8
    //
    // Verified byte-for-byte against spa_pod_builder_add_object() C output.

    /// <summary>
    /// Builds a <c>spa_pod</c> Props object setting only <c>volume</c> (0.0–1.0).
    /// Total: 40 bytes.
    /// </summary>
    public static byte[] BuildPropsVolumeOnlyPod(float volume)
    {
        // 8 header + 8 obj-body + 8 prop-header + 8 float-value = 32 body, 40 total
        var pod = new byte[40];
        WriteU32(pod,  0, 32);               // body size (everything after the 8-byte header)
        WriteU32(pod,  4, SPA_TYPE_Object);  // pod type = generic Object
        WriteU32(pod,  8, SPA_TYPE_OBJECT_Props); // object sub-type
        WriteU32(pod, 12, SPA_PARAM_Props);  // object id = Props
        WriteU32(pod, 16, SPA_PROP_volume);  // prop key
        WriteU32(pod, 20, 0);                // prop flags
        WriteU32(pod, 24, 4);                // value pod body size (float = 4 bytes)
        WriteU32(pod, 28, SPA_TYPE_Float);   // value pod type
        WriteF32(pod, 32, volume);
        WriteU32(pod, 36, 0);                // 4-byte pad to 8-byte alignment
        return pod;
    }

    /// <summary>
    /// Builds a <c>spa_pod</c> Props object setting both <c>volume</c> and <c>mute</c>.
    /// Total: 64 bytes.
    /// </summary>
    public static byte[] BuildPropsVolumeMutePod(float volume, bool mute)
    {
        // 8 header + 8 obj-body + 8+8 vol-prop + 8+8 mute-prop = 56 body, 64 total
        var pod = new byte[64];
        WriteU32(pod,  0, 56);
        WriteU32(pod,  4, SPA_TYPE_Object);
        WriteU32(pod,  8, SPA_TYPE_OBJECT_Props);
        WriteU32(pod, 12, SPA_PARAM_Props);
        // volume prop
        WriteU32(pod, 16, SPA_PROP_volume);
        WriteU32(pod, 20, 0);
        WriteU32(pod, 24, 4);
        WriteU32(pod, 28, SPA_TYPE_Float);
        WriteF32(pod, 32, volume);
        WriteU32(pod, 36, 0);
        // mute prop
        WriteU32(pod, 40, SPA_PROP_mute);
        WriteU32(pod, 44, 0);
        WriteU32(pod, 48, 4);
        WriteU32(pod, 52, SPA_TYPE_Bool);
        WriteU32(pod, 56, mute ? 1u : 0u);
        WriteU32(pod, 60, 0);
        return pod;
    }

    /// <summary>
    /// Builds a <c>spa_pod</c> Props object setting <c>channelVolumes</c> for a stereo
    /// (FL+FR) node. <paramref name="cubicVolume"/> is the raw cubic-scale float value
    /// (i.e. <c>(fraction)^3</c> where fraction is the perceptual 0.0–1.0 level).
    /// Total: 48 bytes.
    /// </summary>
    /// <remarks>
    /// This sets <see cref="SPA_PROP_channelVolumes"/> which is the property that
    /// PipeWire's PulseAudio compatibility layer reads to report sink volume via
    /// <c>pa_sink_info.volume</c>. Unlike <see cref="SPA_PROP_volume"/> (the scalar
    /// master multiplier), this property directly controls the user-visible volume.
    /// </remarks>
    public static byte[] BuildPropsChannelVolumesPod(float cubicVolume)
    {
        // Layout:
        //   8  pod header          (body_size=40, type=Object)
        //   8  object body         (objectType=Props, id=SPA_PARAM_Props)
        //   8  prop key+flags      (key=channelVolumes, flags=0)
        //  24  array value pod:
        //       8  array header    (body_size=16, type=Array)
        //       8  child desc      (child_size=4, child_type=Float)
        //       4  FL float
        //       4  FR float
        // Total: 48 bytes, body_size = 40
        var pod = new byte[48];
        WriteU32(pod,  0, 40);                        // body size
        WriteU32(pod,  4, SPA_TYPE_Object);           // pod type
        WriteU32(pod,  8, SPA_TYPE_OBJECT_Props);     // object sub-type
        WriteU32(pod, 12, SPA_PARAM_Props);           // object id
        WriteU32(pod, 16, SPA_PROP_channelVolumes);   // prop key
        WriteU32(pod, 20, 0);                         // prop flags
        WriteU32(pod, 24, 16);                        // array pod body size: child(8) + 2*4
        WriteU32(pod, 28, SPA_TYPE_Array);            // array pod type
        WriteU32(pod, 32, 4);                         // child element size (float=4)
        WriteU32(pod, 36, SPA_TYPE_Float);            // child element type
        WriteF32(pod, 40, cubicVolume);               // FL
        WriteF32(pod, 44, cubicVolume);               // FR
        return pod;
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset]     = (byte)(value);
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteF32(byte[] buf, int offset, float value)
        => WriteU32(buf, offset, BitConverter.SingleToUInt32Bits(value));

    /// <summary>
    /// Asks the server to instantiate <paramref name="factoryName"/> and return a proxy.
    /// For nodes, use factory <c>"adapter"</c>, type <c>PW_TYPE_INTERFACE_Node</c>, version 3.
    /// For links, use factory <c>"link-factory"</c>, type <c>PW_TYPE_INTERFACE_Link</c>, version 3.
    /// <paramref name="propsPtr"/> is a <c>spa_dict*</c> — pass a <c>pw_properties*</c> directly
    /// (its first field is an embedded <c>spa_dict</c>).
    /// Returns the proxy pointer, or NULL on failure.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_core_create_object")]
    public static extern IntPtr pw_core_create_object(
        IntPtr core,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string factoryName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        uint version,
        IntPtr propsPtr,      // const struct spa_dict* — pass pw_properties* (dict is first field)
        UIntPtr userDataSize);

    /// <summary>
    /// Registers a listener on the core for events including <c>done</c> (sync reply).
    /// <paramref name="hookPtr"/> must point to a zeroed <see cref="SpaHook"/> struct that lives
    /// as long as the listener is active (pin it or keep it in a field).
    /// <paramref name="eventsPtr"/> must point to a <see cref="PwCoreEvents"/> struct.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_core_add_listener")]
    public static extern int pw_core_add_listener(
        IntPtr core,          // pw_core*
        IntPtr hookPtr,       // spa_hook* — must be zeroed, pinned
        IntPtr eventsPtr,     // const pw_core_events*
        IntPtr data);         // user data pointer passed to callbacks

    // ── Registry ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets the global registry object from the core. Returns a <c>pw_registry*</c>.
    /// The caller owns the proxy and must unref/free it via pw_proxy_destroy when done.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_core_get_registry")]
    public static extern IntPtr pw_core_get_registry(
        IntPtr core,          // pw_core*
        uint version,         // PW_VERSION_REGISTRY
        UIntPtr userDataSize);

    /// <summary>
    /// Registers a listener on the registry for <c>global</c> and <c>global_remove</c> events.
    /// <paramref name="hookPtr"/> must point to a zeroed <see cref="SpaHook"/> that lives as long
    /// as the listener is active.
    /// <paramref name="eventsPtr"/> must point to a <see cref="PwRegistryEvents"/> struct.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_registry_add_listener")]
    public static extern int pw_registry_add_listener(
        IntPtr registry,      // pw_registry*
        IntPtr hookPtr,       // spa_hook* — must be zeroed, pinned
        IntPtr eventsPtr,     // const pw_registry_events*
        IntPtr data);         // user data pointer passed to callbacks

    /// <summary>
    /// Destroys a global object by ID via the registry (equivalent to pw_registry_destroy in C).
    /// Used to remove stale volmon_compat_* nodes that have object.linger=1 set.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pw_registry_destroy")]
    public static extern int pw_registry_destroy(
        IntPtr registry,      // pw_registry*
        uint id);             // global ID to destroy

    // ── Managed struct mirrors for spa_hook and event tables ──────────
    //
    // These structs are passed by pointer to pw_*_add_listener().  They must
    // be kept alive (GCHandle or field) for the lifetime of the listener.
    //
    // Layout verified against <spa/utils/hook.h> and <pipewire/core.h> on
    // x86-64 Linux with libpipewire 0.3.x:
    //
    //   spa_hook (48 bytes):
    //     offset  0 — spa_list link  (2 × IntPtr = 16 bytes)
    //     offset 16 — cb             (2 × IntPtr = 16 bytes)
    //     offset 32 — removed fn ptr (IntPtr)
    //     offset 40 — priv ptr       (IntPtr)
    //
    //   pw_registry_events (24 bytes):
    //     offset  0 — uint version
    //     [4 bytes pad]
    //     offset  8 — IntPtr global
    //     offset 16 — IntPtr global_remove
    //
    //   pw_core_events (80 bytes):
    //     offset  0 — uint version
    //     [4 bytes pad]
    //     offset  8 — IntPtr info
    //     offset 16 — IntPtr done
    //     offset 24 — IntPtr ping
    //     offset 32 — IntPtr error
    //     offset 40 — IntPtr remove_id
    //     offset 48 — IntPtr bound_id
    //     offset 56 — IntPtr add_mem
    //     offset 64 — IntPtr remove_mem
    //     offset 72 — IntPtr bound_props

    /// <summary>
    /// Mirror of <c>struct spa_hook</c> (48 bytes on x86-64).
    /// Zero-initialise before use; do NOT modify after passing to add_listener.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    public struct SpaHook
    {
        // Zeroed by default — intentionally no fields exposed.
        // The native code fills in all fields via add_listener.
    }

    // ── Callback delegate types ───────────────────────────────────────

    /// <summary>
    /// Delegate for the <c>global</c> callback in <see cref="PwRegistryEvents"/>.
    /// Called for each global object currently in the registry (initial enumeration)
    /// and whenever a new global is added.
    /// </summary>
    /// <param name="data">User data pointer passed to pw_registry_add_listener.</param>
    /// <param name="id">Global object ID.</param>
    /// <param name="permissions">Object permission bits.</param>
    /// <param name="type">Interface type string (e.g. "PipeWire:Interface:Node").</param>
    /// <param name="version">Interface version.</param>
    /// <param name="props">Pointer to <c>const struct spa_dict</c> — read via spa_dict_lookup.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PwRegistryGlobalCallback(
        IntPtr data,
        uint   id,
        uint   permissions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        uint   version,
        IntPtr props);   // const struct spa_dict*

    /// <summary>
    /// Delegate for the <c>global_remove</c> callback in <see cref="PwRegistryEvents"/>.
    /// Called when a global object is removed from the registry.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PwRegistryGlobalRemoveCallback(IntPtr data, uint id);

    /// <summary>
    /// Delegate for the <c>done</c> callback in <see cref="PwCoreEvents"/>.
    /// Called when the server replies to a <see cref="pw_core_sync"/> request.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PwCoreDoneCallback(IntPtr data, uint id, int seq);

    // ── Event table structs ───────────────────────────────────────────

    /// <summary>
    /// Mirror of <c>struct pw_registry_events</c> (24 bytes on x86-64).
    /// Set <see cref="Version"/> to <see cref="PW_VERSION_REGISTRY_EVENTS"/> (0).
    /// Unused callbacks may be IntPtr.Zero.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PwRegistryEvents
    {
        public uint   Version;         // offset  0 — must be PW_VERSION_REGISTRY_EVENTS
        public uint   _pad;            // offset  4 — alignment padding
        public IntPtr Global;          // offset  8 — PwRegistryGlobalCallback or Zero
        public IntPtr GlobalRemove;    // offset 16 — PwRegistryGlobalRemoveCallback or Zero
    }

    /// <summary>
    /// Mirror of <c>struct pw_core_events</c> (80 bytes on x86-64).
    /// Set <see cref="Version"/> to <see cref="PW_VERSION_CORE_EVENTS"/> (1).
    /// Only the <c>Done</c> slot is used; all others may be IntPtr.Zero.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PwCoreEvents
    {
        public uint   Version;    // offset  0 — must be PW_VERSION_CORE_EVENTS
        public uint   _pad;       // offset  4 — alignment padding
        public IntPtr Info;       // offset  8
        public IntPtr Done;       // offset 16 — PwCoreDoneCallback or Zero
        public IntPtr Ping;       // offset 24
        public IntPtr Error;      // offset 32
        public IntPtr RemoveId;   // offset 40
        public IntPtr BoundId;    // offset 48
        public IntPtr AddMem;     // offset 56
        public IntPtr RemoveMem;  // offset 64
        public IntPtr BoundProps; // offset 72
    }

    // ── spa_dict helpers ─────────────────────────────────────────────

    /// <summary>
    /// Looks up a value in a native <c>spa_dict*</c> by key.
    /// Returns the value string, or null if not found.
    /// </summary>
    public static string? SpaDictLookup(IntPtr dictPtr, string key)
    {
        if (dictPtr == IntPtr.Zero) return null;

        // spa_dict layout:
        //   uint flags    (4 bytes)
        //   uint n_items  (4 bytes)
        //   spa_dict_item* items pointer (8 bytes on x64)
        //
        // spa_dict_item layout:
        //   char* key   (8 bytes)
        //   char* value (8 bytes)

        uint nItems = (uint)Marshal.ReadInt32(dictPtr, 4);
        IntPtr itemsPtr = Marshal.ReadIntPtr(dictPtr, 8);

        for (int i = 0; i < (int)nItems; i++)
        {
            // Each spa_dict_item is 16 bytes (2 × ptr) on x64
            IntPtr itemPtr  = itemsPtr + i * 16;
            IntPtr keyPtr   = Marshal.ReadIntPtr(itemPtr, 0);
            IntPtr valuePtr = Marshal.ReadIntPtr(itemPtr, 8);

            if (keyPtr == IntPtr.Zero) continue;
            string? k = Marshal.PtrToStringUTF8(keyPtr);
            if (k == key)
                return valuePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(valuePtr);
        }
        return null;
    }
}
