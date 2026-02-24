using System.Runtime.InteropServices;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// P/Invoke bindings for libpulse (PulseAudio client library).
/// Struct layouts verified against PulseAudio headers on x86_64 Linux.
/// All opaque handles are represented as <see cref="IntPtr"/>.
/// </summary>
internal static partial class LibPulse
{
    private const string Lib = "libpulse.so.0";

    // ── Constants ────────────────────────────────────────────────────

    public const uint PA_VOLUME_NORM  = 0x10000U;   // 65536 — 0 dB (100 %)
    public const uint PA_VOLUME_MUTED = 0U;
    public const int  PA_CHANNELS_MAX = 32;
    public const uint PA_INVALID_INDEX = uint.MaxValue;

    // ── Enums ────────────────────────────────────────────────────────

    public enum pa_context_state_t
    {
        Unconnected,   // 0
        Connecting,    // 1
        Authorizing,   // 2
        SettingName,   // 3
        Ready,         // 4
        Failed,        // 5
        Terminated     // 6
    }

    public enum pa_operation_state_t
    {
        Running,       // 0
        Done,          // 1
        Cancelled      // 2
    }

    [Flags]
    public enum pa_context_flags_t
    {
        NoFlags     = 0x0000,
        NoAutospawn = 0x0001,
        NoFail      = 0x0002
    }

    [Flags]
    public enum pa_subscription_mask_t : uint
    {
        Null         = 0x0000,
        Sink         = 0x0001,
        Source       = 0x0002,
        SinkInput    = 0x0004,
        SourceOutput = 0x0008,
        Module       = 0x0010,
        Client       = 0x0020,
        SampleCache  = 0x0040,
        Server       = 0x0080,
        Card         = 0x0200,
        All          = 0x02FF
    }

    public enum pa_subscription_event_type_t : uint
    {
        Sink         = 0x0000,
        Source       = 0x0001,
        SinkInput    = 0x0002,
        SourceOutput = 0x0003,
        Module       = 0x0004,
        Client       = 0x0005,
        SampleCache  = 0x0006,
        Server       = 0x0007,
        Card         = 0x0009,

        FacilityMask = 0x000F,

        New    = 0x0000,
        Change = 0x0010,
        Remove = 0x0020,

        TypeMask = 0x0030
    }

    // ── Structs (verified byte offsets on x86_64 Linux) ──────────────

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    public struct pa_sample_spec
    {
        [FieldOffset(0)] public int format;      // pa_sample_format_t
        [FieldOffset(4)] public uint rate;
        [FieldOffset(8)] public byte channels;
    }

    [StructLayout(LayoutKind.Explicit, Size = 132)]
    public struct pa_channel_map
    {
        [FieldOffset(0)] public byte channels;
        // map[32] starts at offset 4 — we don't need to read individual positions
    }

    [StructLayout(LayoutKind.Explicit, Size = 132)]
    public unsafe struct pa_cvolume
    {
        [FieldOffset(0)] public byte channels;
        [FieldOffset(4)] public fixed uint values[PA_CHANNELS_MAX];
    }

    /// <summary>
    /// pa_sink_info — 416 bytes on x86_64. Fields we don't read are left as padding.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    public struct pa_sink_info
    {
        [FieldOffset(  0)] public IntPtr name;               // const char*
        [FieldOffset(  8)] public uint   index;
        [FieldOffset( 16)] public IntPtr description;        // const char*
        [FieldOffset( 24)] public pa_sample_spec sample_spec;
        [FieldOffset( 36)] public pa_channel_map channel_map;
        [FieldOffset(168)] public uint   owner_module;
        [FieldOffset(172)] public pa_cvolume volume;
        [FieldOffset(304)] public int    mute;
        [FieldOffset(308)] public uint   monitor_source;
        [FieldOffset(312)] public IntPtr monitor_source_name; // const char*
        [FieldOffset(320)] public ulong  latency;            // pa_usec_t
        [FieldOffset(328)] public IntPtr driver;             // const char*
        [FieldOffset(336)] public int    flags;              // pa_sink_flags_t
        [FieldOffset(344)] public IntPtr proplist;           // pa_proplist*
        [FieldOffset(352)] public ulong  configured_latency;
        [FieldOffset(360)] public uint   base_volume;
        [FieldOffset(364)] public int    state;              // pa_sink_state_t
        [FieldOffset(368)] public uint   n_volume_steps;
        [FieldOffset(372)] public uint   card;
        [FieldOffset(376)] public uint   n_ports;
        [FieldOffset(384)] public IntPtr ports;
        [FieldOffset(392)] public IntPtr active_port;
        [FieldOffset(400)] public byte   n_formats;
        [FieldOffset(408)] public IntPtr formats;
    }

    /// <summary>
    /// pa_source_info — 416 bytes on x86_64. Identical layout to pa_sink_info
    /// except monitor_of_sink / monitor_of_sink_name instead of monitor_source / monitor_source_name.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    public struct pa_source_info
    {
        [FieldOffset(  0)] public IntPtr name;
        [FieldOffset(  8)] public uint   index;
        [FieldOffset( 16)] public IntPtr description;
        [FieldOffset( 24)] public pa_sample_spec sample_spec;
        [FieldOffset( 36)] public pa_channel_map channel_map;
        [FieldOffset(168)] public uint   owner_module;
        [FieldOffset(172)] public pa_cvolume volume;
        [FieldOffset(304)] public int    mute;
        [FieldOffset(308)] public uint   monitor_of_sink;
        [FieldOffset(312)] public IntPtr monitor_of_sink_name;
        [FieldOffset(320)] public ulong  latency;
        [FieldOffset(328)] public IntPtr driver;
        [FieldOffset(336)] public int    flags;
        [FieldOffset(344)] public IntPtr proplist;
        [FieldOffset(352)] public ulong  configured_latency;
        [FieldOffset(360)] public uint   base_volume;
        [FieldOffset(364)] public int    state;
        [FieldOffset(368)] public uint   n_volume_steps;
        [FieldOffset(372)] public uint   card;
        [FieldOffset(376)] public uint   n_ports;
        [FieldOffset(384)] public IntPtr ports;
        [FieldOffset(392)] public IntPtr active_port;
        [FieldOffset(400)] public byte   n_formats;
        [FieldOffset(408)] public IntPtr formats;
    }

    /// <summary>
    /// pa_sink_input_info — 376 bytes on x86_64.
    /// Note: volume is at offset 172 (before buffer_usec), unlike pa_source_output_info.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 376)]
    public struct pa_sink_input_info
    {
        [FieldOffset(  0)] public uint   index;
        [FieldOffset(  8)] public IntPtr name;               // const char*
        [FieldOffset( 16)] public uint   owner_module;
        [FieldOffset( 20)] public uint   client;
        [FieldOffset( 24)] public uint   sink;
        [FieldOffset( 28)] public pa_sample_spec sample_spec;
        [FieldOffset( 40)] public pa_channel_map channel_map;
        [FieldOffset(172)] public pa_cvolume volume;
        [FieldOffset(304)] public ulong  buffer_usec;
        [FieldOffset(312)] public ulong  sink_usec;
        [FieldOffset(320)] public IntPtr resample_method;
        [FieldOffset(328)] public IntPtr driver;
        [FieldOffset(336)] public int    mute;
        [FieldOffset(344)] public IntPtr proplist;           // pa_proplist*
        [FieldOffset(352)] public int    corked;
        [FieldOffset(356)] public int    has_volume;
        [FieldOffset(360)] public int    volume_writable;
        [FieldOffset(368)] public IntPtr format;
    }

    /// <summary>
    /// pa_source_output_info — 376 bytes on x86_64.
    /// Note: volume is at offset 220 (after corked), unlike pa_sink_input_info.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 376)]
    public struct pa_source_output_info
    {
        [FieldOffset(  0)] public uint   index;
        [FieldOffset(  8)] public IntPtr name;
        [FieldOffset( 16)] public uint   owner_module;
        [FieldOffset( 20)] public uint   client;
        [FieldOffset( 24)] public uint   source;
        [FieldOffset( 28)] public pa_sample_spec sample_spec;
        [FieldOffset( 40)] public pa_channel_map channel_map;
        [FieldOffset(176)] public ulong  buffer_usec;
        [FieldOffset(184)] public ulong  source_usec;
        [FieldOffset(192)] public IntPtr resample_method;
        [FieldOffset(200)] public IntPtr driver;
        [FieldOffset(208)] public IntPtr proplist;
        [FieldOffset(216)] public int    corked;
        [FieldOffset(220)] public pa_cvolume volume;
        [FieldOffset(352)] public int    mute;
        [FieldOffset(356)] public int    has_volume;
        [FieldOffset(360)] public int    volume_writable;
        [FieldOffset(368)] public IntPtr format;
    }

    // ── Callback Delegates ───────────────────────────────────────────
    // IMPORTANT: instances must be stored to prevent GC collection.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_context_notify_cb_t(IntPtr context, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_context_success_cb_t(IntPtr context, int success, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_context_subscribe_cb_t(
        IntPtr context, pa_subscription_event_type_t type, uint idx, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_sink_info_cb_t(
        IntPtr context, IntPtr info, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_source_info_cb_t(
        IntPtr context, IntPtr info, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_sink_input_info_cb_t(
        IntPtr context, IntPtr info, int eol, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_source_output_info_cb_t(
        IntPtr context, IntPtr info, int eol, IntPtr userdata);

    // ── Non-threaded Main Loop ──────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_mainloop_new();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_mainloop_free(IntPtr m);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_mainloop_get_api(IntPtr m);

    /// <summary>
    /// Run a single iteration of the main loop. Returns negative on error/quit,
    /// or the number of sources dispatched. When <paramref name="block"/> is 1,
    /// blocks until events are ready; when 0, returns immediately.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_mainloop_iterate(IntPtr m, int block, IntPtr retval);

    /// <summary>
    /// Interrupt a blocking <see cref="pa_mainloop_iterate"/> call from another thread.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_mainloop_wakeup(IntPtr m);

    // ── Proplist ─────────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_proplist_new();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_proplist_free(IntPtr p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int pa_proplist_sets(IntPtr p,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr pa_proplist_gets(IntPtr p,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key);

    // ── Context ──────────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_new_with_proplist(IntPtr mainloop_api,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? name, IntPtr proplist);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_context_unref(IntPtr c);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_context_set_state_callback(IntPtr c,
        pa_context_notify_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern pa_context_state_t pa_context_get_state(IntPtr c);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_context_connect(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        pa_context_flags_t flags, IntPtr spawn_api);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_context_disconnect(IntPtr c);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_context_errno(IntPtr c);

    // ── Subscribe ────────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_context_set_subscribe_callback(IntPtr c,
        pa_context_subscribe_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_subscribe(IntPtr c,
        pa_subscription_mask_t mask, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Introspection: Sinks ─────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_sink_info_list(IntPtr c,
        pa_sink_info_cb_t cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_sink_volume_by_name(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref pa_cvolume volume, pa_context_success_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_sink_mute_by_name(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int mute, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Introspection: Sources ───────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_source_info_list(IntPtr c,
        pa_source_info_cb_t cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_source_volume_by_name(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref pa_cvolume volume, pa_context_success_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_source_mute_by_name(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int mute, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Introspection: Sink Inputs ───────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_sink_input_info_list(IntPtr c,
        pa_sink_input_info_cb_t cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_sink_input_volume(IntPtr c,
        uint idx, ref pa_cvolume volume, pa_context_success_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_sink_input_mute(IntPtr c,
        uint idx, int mute, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Introspection: Source Outputs ────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_source_output_info_list(IntPtr c,
        pa_source_output_info_cb_t cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_source_output_volume(IntPtr c,
        uint idx, ref pa_cvolume volume, pa_context_success_cb_t? cb, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_set_source_output_mute(IntPtr c,
        uint idx, int mute, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Module management (for CompatibilityMode null-sink) ──────────

    /// <summary>
    /// Callback fired when <c>pa_context_load_module</c> completes.
    /// <paramref name="idx"/> is the new module index on success,
    /// or <c>PA_INVALID_INDEX</c> (uint.MaxValue) on failure.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_context_index_cb_t(IntPtr c, uint idx, IntPtr userdata);

    /// <summary>Loads a module by name and argument string. The result index is delivered to the callback.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_load_module(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? argument,
        pa_context_index_cb_t? cb, IntPtr userdata);

    /// <summary>Unloads a previously loaded module by index.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_unload_module(IntPtr c,
        uint idx, pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Module introspection (for stale-module cleanup on startup) ───

    [StructLayout(LayoutKind.Sequential)]
    public struct pa_module_info
    {
        public uint index;
        public IntPtr name;       // const char*
        public IntPtr argument;   // const char*
        public uint n_used;
        // pa_proplist* and padding follow — we don't need them
        private IntPtr _proplist;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void pa_module_info_cb_t(
        IntPtr c, IntPtr info, int eol, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_module_info_list(
        IntPtr c, pa_module_info_cb_t cb, IntPtr userdata);

    // ── Sink input routing ───────────────────────────────────────────

    /// <summary>Moves a sink input to a different sink (identified by index).</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_move_sink_input_by_index(IntPtr c,
        uint idx, uint sink_idx, pa_context_success_cb_t? cb, IntPtr userdata);

    /// <summary>Moves a sink input to a different sink (identified by name).</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_move_sink_input_by_name(IntPtr c,
        uint idx, [MarshalAs(UnmanagedType.LPUTF8Str)] string sink_name,
        pa_context_success_cb_t? cb, IntPtr userdata);

    // ── Sink info by name ────────────────────────────────────────────

    /// <summary>Fetches info for a single sink by name.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_context_get_sink_info_by_name(IntPtr c,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        pa_sink_info_cb_t cb, IntPtr userdata);

    // ── Operations ───────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_operation_unref(IntPtr o);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern pa_operation_state_t pa_operation_get_state(IntPtr o);

    // ── Volume Helpers ───────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_cvolume_set(ref pa_cvolume a, uint channels, uint v);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint pa_cvolume_avg(ref pa_cvolume a);

    // ── Error ────────────────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_strerror(int error);

    // ── libc ───────────────────────────────────────────────────────────
    // Environment.SetEnvironmentVariable only updates .NET's internal dictionary;
    // native code (like libpulse) calls getenv() which reads the C runtime environ.
    // We need the real POSIX setenv so PULSE_NO_SPAWN is visible to libpulse.

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern int setenv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int overwrite);

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Reads a UTF-8 string from a native pointer. Returns null if the pointer is zero.</summary>
    public static string? PtrToStringUtf8(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>
    /// Computes the average volume across all channels and converts to 0-100 percentage.
    /// </summary>
    public static unsafe int CvolumeToPercent(ref pa_cvolume vol)
    {
        uint avg = pa_cvolume_avg(ref vol);
        return (int)Math.Round(avg * 100.0 / PA_VOLUME_NORM);
    }

    /// <summary>
    /// Creates a <see cref="pa_cvolume"/> with all channels set to the given 0-100 percentage.
    /// Preserves the original channel count.
    /// </summary>
    public static pa_cvolume PercentToCvolume(byte channels, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        uint raw = (uint)Math.Round(percent * (double)PA_VOLUME_NORM / 100.0);
        var vol = new pa_cvolume();
        pa_cvolume_set(ref vol, channels, raw);
        return vol;
    }
}
