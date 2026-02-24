using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// P/Invoke bindings for macOS CoreAudio HAL and CoreFoundation frameworks.
/// Provides the low-level native calls needed by <see cref="MacOsBackend"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class LibCoreAudio
{
    // ── Framework paths ──────────────────────────────────────────────

    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // ── AudioObject property selectors (FourCC as UInt32) ────────────

    // System object
    public const uint kAudioObjectSystemObject = 1;

    // Property selectors
    public const uint kAudioHardwarePropertyDevices = 0x64657623;           // 'dev#'
    public const uint kAudioHardwarePropertyDefaultOutputDevice = 0x646F7574; // 'dout'
    public const uint kAudioHardwarePropertyDefaultInputDevice = 0x64696E20;  // 'din '

    public const uint kAudioObjectPropertyName = 0x6C6E616D;                // 'lnam'
    public const uint kAudioDevicePropertyDeviceUID = 0x75696420;           // 'uid '
    public const uint kAudioDevicePropertyStreams = 0x73746D23;             // 'stm#'

    // Volume / mute
    public const uint kAudioDevicePropertyVolumeScalar = 0x766F6C6D;       // 'volm'
    public const uint kAudioDevicePropertyMute = 0x6D757465;               // 'mute'
    public const uint kAudioDevicePropertyVirtualMasterVolume = 0x766D7663; // 'vmvc'

    // Data source (for human-readable output name)
    public const uint kAudioDevicePropertyDataSource = 0x73737263;          // 'ssrc'
    public const uint kAudioDevicePropertyDataSourceNameForIDCFString = 0x6C73636E; // 'lscn'

    // Transport type
    public const uint kAudioDevicePropertyTransportType = 0x7472616E;       // 'tran'

    // Alive check
    public const uint kAudioDevicePropertyDeviceIsAlive = 0x6C697665;       // 'live'

    // ── Scopes ───────────────────────────────────────────────────────

    public const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62;        // 'glob'
    public const uint kAudioObjectPropertyScopeOutput = 0x6F757470;        // 'outp'
    public const uint kAudioObjectPropertyScopeInput = 0x696E7074;         // 'inpt'

    // ── Elements ─────────────────────────────────────────────────────

    public const uint kAudioObjectPropertyElementMain = 0;

    // ── Wildcards (for listeners) ────────────────────────────────────

    public const uint kAudioObjectPropertySelectorWildcard = 0x2A2A2A2A;   // '****'
    public const uint kAudioObjectPropertyScopeWildcard = 0x2A2A2A2A;      // '****'
    public const uint kAudioObjectPropertyElementWildcard = 0xFFFFFFFF;

    // ── OSStatus codes ───────────────────────────────────────────────

    public const int kAudioHardwareNoError = 0;
    public const int kAudioHardwareUnknownPropertyError = 0x77686F3F;       // 'who?'
    public const int kAudioHardwareBadObjectError = 0x216F626A;             // '!obj'

    // ── Structs ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;

        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            mSelector = selector;
            mScope = scope;
            mElement = element;
        }
    }

    // ── Delegates ────────────────────────────────────────────────────

    /// <summary>
    /// Callback for AudioObjectAddPropertyListener.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AudioObjectPropertyListenerProc(
        uint objectId,
        uint numberAddresses,
        IntPtr addresses, // AudioObjectPropertyAddress*
        IntPtr clientData);

    // ── CoreAudio HAL functions ──────────────────────────────────────

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyDataSize")]
    public static extern int AudioObjectGetPropertyDataSize(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        uint qualifierDataSize,
        IntPtr qualifierData,
        out uint dataSize);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    public static extern int AudioObjectGetPropertyData(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        uint qualifierDataSize,
        IntPtr qualifierData,
        ref uint dataSize,
        IntPtr data);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectSetPropertyData")]
    public static extern int AudioObjectSetPropertyData(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        uint qualifierDataSize,
        IntPtr qualifierData,
        uint dataSize,
        IntPtr data);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectHasProperty")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AudioObjectHasProperty(
        uint objectId,
        ref AudioObjectPropertyAddress address);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectAddPropertyListener")]
    public static extern int AudioObjectAddPropertyListener(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        AudioObjectPropertyListenerProc listener,
        IntPtr clientData);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectRemovePropertyListener")]
    public static extern int AudioObjectRemovePropertyListener(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        AudioObjectPropertyListenerProc listener,
        IntPtr clientData);

    // ── CoreFoundation (CFString) ────────────────────────────────────

    [DllImport(CoreFoundationLib, EntryPoint = "CFStringGetLength")]
    public static extern nint CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundationLib, EntryPoint = "CFStringGetCharacters")]
    public static extern void CFStringGetCharacters(IntPtr theString, CFRange range, IntPtr buffer);

    [DllImport(CoreFoundationLib, EntryPoint = "CFRelease")]
    public static extern void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange
    {
        public nint location;
        public nint length;

        public CFRange(nint location, nint length)
        {
            this.location = location;
            this.length = length;
        }
    }

    // ── Managed helpers ──────────────────────────────────────────────

    /// <summary>
    /// Converts a CFStringRef to a managed string. Does NOT release the CFString.
    /// </summary>
    public static string? CFStringToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;

        var length = CFStringGetLength(cfString);
        if (length <= 0) return string.Empty;

        var bufSize = (int)length * sizeof(char);
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            CFStringGetCharacters(cfString, new CFRange(0, length), buf);
            return Marshal.PtrToStringUni(buf, (int)length);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Gets a CFString property from an AudioObject and returns it as a managed string.
    /// </summary>
    public static string? GetStringProperty(uint objectId, uint selector, uint scope = kAudioObjectPropertyScopeGlobal)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, kAudioObjectPropertyElementMain);

        if (!AudioObjectHasProperty(objectId, ref address))
            return null;

        uint dataSize = (uint)IntPtr.Size;
        IntPtr cfStringRef = IntPtr.Zero;

        var status = AudioObjectGetPropertyData(
            objectId,
            ref address,
            0, IntPtr.Zero,
            ref dataSize,
            ref cfStringRef);

        if (status != kAudioHardwareNoError || cfStringRef == IntPtr.Zero)
            return null;

        try
        {
            return CFStringToManaged(cfStringRef);
        }
        finally
        {
            CFRelease(cfStringRef);
        }
    }

    /// <summary>
    /// Overload of GetPropertyData that reads into a typed pointer via ref.
    /// </summary>
    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    public static extern int AudioObjectGetPropertyData(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        uint qualifierDataSize,
        IntPtr qualifierData,
        ref uint dataSize,
        ref IntPtr data);

    /// <summary>
    /// Gets a Float32 property from an AudioObject.
    /// </summary>
    public static float? GetFloat32Property(uint objectId, uint selector, uint scope, uint element = kAudioObjectPropertyElementMain)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, element);

        if (!AudioObjectHasProperty(objectId, ref address))
            return null;

        uint dataSize = sizeof(float);
        float value = 0f;
        var ptr = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            var status = AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref dataSize, ptr);
            if (status != kAudioHardwareNoError)
                return null;
            value = Marshal.PtrToStructure<float>(ptr);
            return value;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Sets a Float32 property on an AudioObject.
    /// </summary>
    public static int SetFloat32Property(uint objectId, uint selector, uint scope, float value, uint element = kAudioObjectPropertyElementMain)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, element);
        var ptr = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            return AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(float), ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Gets a UInt32 property from an AudioObject.
    /// </summary>
    public static uint? GetUInt32Property(uint objectId, uint selector, uint scope, uint element = kAudioObjectPropertyElementMain)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, element);

        if (!AudioObjectHasProperty(objectId, ref address))
            return null;

        uint dataSize = sizeof(uint);
        var ptr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            var status = AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref dataSize, ptr);
            if (status != kAudioHardwareNoError)
                return null;
            return (uint)Marshal.ReadInt32(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Sets a UInt32 property on an AudioObject.
    /// </summary>
    public static int SetUInt32Property(uint objectId, uint selector, uint scope, uint value, uint element = kAudioObjectPropertyElementMain)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, element);
        var ptr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(ptr, (int)value);
            return AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(uint), ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Returns all AudioObjectIDs for a given array property (e.g. device list, stream list).
    /// </summary>
    public static uint[]? GetAudioObjectArray(uint objectId, uint selector, uint scope = kAudioObjectPropertyScopeGlobal)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, kAudioObjectPropertyElementMain);

        var status = AudioObjectGetPropertyDataSize(objectId, ref address, 0, IntPtr.Zero, out uint dataSize);
        if (status != kAudioHardwareNoError || dataSize == 0)
            return null;

        var count = dataSize / sizeof(uint);
        var ptr = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            status = AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref dataSize, ptr);
            if (status != kAudioHardwareNoError)
                return null;

            var result = new uint[count];
            for (int i = 0; i < count; i++)
                result[i] = (uint)Marshal.ReadInt32(ptr, i * sizeof(uint));
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
