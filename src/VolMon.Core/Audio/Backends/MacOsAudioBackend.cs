using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VolMon.Core.Audio.Backends;

using VolMon.Core.Audio;

/// <summary>
/// macOS audio backend using CoreAudio HAL (Hardware Abstraction Layer) via P/Invoke.
///
/// Supports device-level volume/mute control and device enumeration. Per-application
/// stream control is NOT available on macOS — CoreAudio does not expose per-app
/// audio sessions. Stream methods return empty lists / no-op.
///
/// Uses the "virtual master volume" selector ('vmvc') which the system synthesizes
/// from per-channel controls, preserving stereo balance.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsAudioBackend : IAudioBackend
{
    // ── CoreAudio HAL P/Invoke ───────────────────────────────────────

    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, out uint data);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, out float data);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, out IntPtr data);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, uint dataSize, ref float data);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, uint dataSize, ref uint data);

    [DllImport(CoreAudioLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioObjectHasProperty(
        uint objectId, ref AudioObjectPropertyAddress address);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectAddPropertyListener(
        uint objectId, ref AudioObjectPropertyAddress address,
        AudioObjectPropertyListenerProc listener, IntPtr clientData);

    [DllImport(CoreAudioLib)]
    private static extern int AudioObjectRemovePropertyListener(
        uint objectId, ref AudioObjectPropertyAddress address,
        AudioObjectPropertyListenerProc listener, IntPtr clientData);

    // CoreFoundation for CFString handling
    [DllImport(CoreFoundationLib)]
    private static extern long CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundationLib)]
    private static extern void CFStringGetCharacters(IntPtr theString, CFRange range, IntPtr buffer);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public long Location;
        public long Length;
    }

    // Listener callback delegate — must be kept alive to prevent GC
    private delegate int AudioObjectPropertyListenerProc(
        uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    // ── Constants ────────────────────────────────────────────────────

    private const uint kAudioObjectSystemObject = 1;

    // Scopes
    private const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62; // 'glob'
    private const uint kAudioObjectPropertyScopeOutput = 0x6F757470; // 'outp'
    private const uint kAudioObjectPropertyScopeInput  = 0x696E7074; // 'inpt'

    // System-level selectors
    private const uint kAudioHardwarePropertyDevices              = 0x64657623; // 'dev#'
    private const uint kAudioHardwarePropertyDefaultOutputDevice  = 0x644F7574; // 'dOut'
    private const uint kAudioHardwarePropertyDefaultInputDevice   = 0x64496E20; // 'dIn '

    // Device selectors
    private const uint kAudioObjectPropertyName        = 0x6C6E616D; // 'lnam'
    private const uint kAudioDevicePropertyDeviceUID    = 0x75696420; // 'uid '
    private const uint kAudioDevicePropertyStreams      = 0x73746D23; // 'stm#'
    private const uint kAudioDevicePropertyMute         = 0x6D757465; // 'mute'
    private const uint kAudioDevicePropertyVolumeScalar = 0x766F6C6D; // 'volm'

    // Virtual master volume — synthesized from per-channel controls
    private const uint kVirtualMasterVolume = 0x766D7663; // 'vmvc'

    private const int kAudioHardwareNoError = 0;

    // ── State ────────────────────────────────────────────────────────

    private AudioObjectPropertyListenerProc? _deviceListListener;
    private readonly List<uint> _monitoredDeviceIds = new();
    private AudioObjectPropertyListenerProc? _volumeListener;
    private AudioObjectPropertyListenerProc? _muteListener;

#pragma warning disable CS0067 // Per-app stream events are not available on macOS
    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
#pragma warning restore CS0067
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    // ── Streams (not supported on macOS) ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// macOS does not expose per-application audio sessions via CoreAudio.
    /// Returns an empty list. Per-app volume control would require a virtual
    /// audio driver (BlackHole, Loopback) or a privileged audio plugin.
    /// </remarks>
    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AudioStream>>(Array.Empty<AudioStream>());

    public Task<IReadOnlyList<VolMon.Core.Audio.AudioProcess>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<VolMon.Core.Audio.AudioProcess>)Array.Empty<VolMon.Core.Audio.AudioProcess>());

    /// <inheritdoc/>
    /// <remarks>No-op on macOS — per-app stream control is not available.</remarks>
    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op on macOS — per-app stream control is not available.</remarks>
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) =>
        Task.CompletedTask;

    // ── Devices ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult<IReadOnlyList<AudioDevice>>(Array.Empty<AudioDevice>());

        var devices = new List<AudioDevice>();
        var deviceIds = GetAllDeviceIds();

        foreach (var deviceId in deviceIds)
        {
            var hasOutput = HasStreamsInScope(deviceId, kAudioObjectPropertyScopeOutput);
            var hasInput = HasStreamsInScope(deviceId, kAudioObjectPropertyScopeInput);

            if (hasOutput)
                AddDevice(devices, deviceId, DeviceType.Sink, kAudioObjectPropertyScopeOutput);
            if (hasInput)
                AddDevice(devices, deviceId, DeviceType.Source, kAudioObjectPropertyScopeInput);
        }

        return Task.FromResult<IReadOnlyList<AudioDevice>>(devices);
    }

    /// <inheritdoc/>
    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        volume = Math.Clamp(volume, 0, 100);
        var deviceId = FindDeviceByUid(deviceName);
        if (deviceId == 0) return Task.CompletedTask;

        var scope = HasStreamsInScope(deviceId, kAudioObjectPropertyScopeOutput)
            ? kAudioObjectPropertyScopeOutput
            : kAudioObjectPropertyScopeInput;

        SetVolume(deviceId, scope, volume / 100f);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        var deviceId = FindDeviceByUid(deviceName);
        if (deviceId == 0) return Task.CompletedTask;

        var scope = HasStreamsInScope(deviceId, kAudioObjectPropertyScopeOutput)
            ? kAudioObjectPropertyScopeOutput
            : kAudioObjectPropertyScopeInput;

        SetMute(deviceId, scope, muted);
        return Task.CompletedTask;
    }

    // ── Monitoring ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        // Listen for device list changes (add/remove)
        _deviceListListener = OnDeviceListChanged;
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioHardwarePropertyDevices,
            Scope = kAudioObjectPropertyScopeGlobal,
            Element = 0
        };
        AudioObjectAddPropertyListener(kAudioObjectSystemObject, ref address, _deviceListListener, IntPtr.Zero);

        // Monitor volume/mute on all current devices
        _volumeListener = OnVolumeChanged;
        _muteListener = OnMuteChanged;
        foreach (var deviceId in GetAllDeviceIds())
        {
            MonitorDevice(deviceId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopMonitoringAsync()
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        // Remove device list listener
        if (_deviceListListener is not null)
        {
            var address = new AudioObjectPropertyAddress
            {
                Selector = kAudioHardwarePropertyDevices,
                Scope = kAudioObjectPropertyScopeGlobal,
                Element = 0
            };
            AudioObjectRemovePropertyListener(kAudioObjectSystemObject, ref address, _deviceListListener, IntPtr.Zero);
        }

        // Remove per-device listeners
        UnmonitorAllDevices();

        return Task.CompletedTask;
    }

    // ── Private: device enumeration ──────────────────────────────────

    private static uint[] GetAllDeviceIds()
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioHardwarePropertyDevices,
            Scope = kAudioObjectPropertyScopeGlobal,
            Element = 0
        };

        if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, ref address, 0, IntPtr.Zero, out var size) != kAudioHardwareNoError)
            return [];

        var count = (int)(size / sizeof(uint));
        if (count == 0) return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(kAudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != kAudioHardwareNoError)
                return [];

            var ids = new uint[count];
            for (int i = 0; i < count; i++)
                ids[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool HasStreamsInScope(uint deviceId, uint scope)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioDevicePropertyStreams,
            Scope = scope,
            Element = 0
        };

        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var size) != kAudioHardwareNoError)
            return false;

        return size > 0;
    }

    private static void AddDevice(List<AudioDevice> list, uint deviceId, DeviceType type, uint scope)
    {
        try
        {
            var uid = GetDeviceUid(deviceId);
            var name = GetDeviceName(deviceId);
            if (uid is null) return;

            var volume = GetVolume(deviceId, scope);
            var muted = GetMute(deviceId, scope);

            list.Add(new AudioDevice
            {
                Id = deviceId.ToString(),
                Name = uid,         // UID is stable across reboots
                Description = name, // Human-readable name
                Type = type,
                Volume = (int)Math.Round(volume * 100),
                Muted = muted
            });
        }
        catch { /* skip inaccessible device */ }
    }

    private static uint FindDeviceByUid(string uid)
    {
        foreach (var deviceId in GetAllDeviceIds())
        {
            if (string.Equals(GetDeviceUid(deviceId), uid, StringComparison.Ordinal))
                return deviceId;
        }
        return 0;
    }

    // ── Private: property getters/setters ────────────────────────────

    private static string? GetDeviceUid(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioDevicePropertyDeviceUID,
            Scope = kAudioObjectPropertyScopeGlobal,
            Element = 0
        };

        uint size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, out IntPtr cfString) != kAudioHardwareNoError)
            return null;

        return CfStringToManaged(cfString);
    }

    private static string? GetDeviceName(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioObjectPropertyName,
            Scope = kAudioObjectPropertyScopeGlobal,
            Element = 0
        };

        uint size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, out IntPtr cfString) != kAudioHardwareNoError)
            return null;

        return CfStringToManaged(cfString);
    }

    private static float GetVolume(uint deviceId, uint scope)
    {
        // Try virtual master first — works on most devices
        var address = new AudioObjectPropertyAddress
        {
            Selector = kVirtualMasterVolume,
            Scope = scope,
            Element = 0
        };

        if (AudioObjectHasProperty(deviceId, ref address))
        {
            uint size = sizeof(float);
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, out float vol) == kAudioHardwareNoError)
                return Math.Clamp(vol, 0f, 1f);
        }

        // Fallback: per-channel volume on channel 1 (left)
        address.Selector = kAudioDevicePropertyVolumeScalar;
        address.Element = 1;
        if (AudioObjectHasProperty(deviceId, ref address))
        {
            uint size = sizeof(float);
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, out float vol) == kAudioHardwareNoError)
                return Math.Clamp(vol, 0f, 1f);
        }

        return 1f;
    }

    private static bool GetMute(uint deviceId, uint scope)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioDevicePropertyMute,
            Scope = scope,
            Element = 0 // Master element
        };

        if (!AudioObjectHasProperty(deviceId, ref address))
            return false;

        uint size = sizeof(uint);
        if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, out uint muted) != kAudioHardwareNoError)
            return false;

        return muted != 0;
    }

    private static void SetVolume(uint deviceId, uint scope, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);

        // Try virtual master first
        var address = new AudioObjectPropertyAddress
        {
            Selector = kVirtualMasterVolume,
            Scope = scope,
            Element = 0
        };

        if (AudioObjectHasProperty(deviceId, ref address))
        {
            AudioObjectSetPropertyData(deviceId, ref address, 0, IntPtr.Zero, sizeof(float), ref volume);
            return;
        }

        // Fallback: set both stereo channels
        address.Selector = kAudioDevicePropertyVolumeScalar;
        for (uint ch = 1; ch <= 2; ch++)
        {
            address.Element = ch;
            if (AudioObjectHasProperty(deviceId, ref address))
                AudioObjectSetPropertyData(deviceId, ref address, 0, IntPtr.Zero, sizeof(float), ref volume);
        }
    }

    private static void SetMute(uint deviceId, uint scope, bool muted)
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = kAudioDevicePropertyMute,
            Scope = scope,
            Element = 0
        };

        if (!AudioObjectHasProperty(deviceId, ref address))
            return;

        uint value = muted ? 1u : 0u;
        AudioObjectSetPropertyData(deviceId, ref address, 0, IntPtr.Zero, sizeof(uint), ref value);
    }

    // ── Private: monitoring helpers ──────────────────────────────────

    private void MonitorDevice(uint deviceId)
    {
        if (_volumeListener is null || _muteListener is null) return;

        // Monitor virtual master volume
        var volAddress = new AudioObjectPropertyAddress
        {
            Selector = kVirtualMasterVolume,
            Scope = kAudioObjectPropertyScopeOutput,
            Element = 0
        };

        if (AudioObjectHasProperty(deviceId, ref volAddress))
            AudioObjectAddPropertyListener(deviceId, ref volAddress, _volumeListener, IntPtr.Zero);

        // Also monitor input scope
        volAddress.Scope = kAudioObjectPropertyScopeInput;
        if (AudioObjectHasProperty(deviceId, ref volAddress))
            AudioObjectAddPropertyListener(deviceId, ref volAddress, _volumeListener, IntPtr.Zero);

        // Monitor mute
        var muteAddress = new AudioObjectPropertyAddress
        {
            Selector = kAudioDevicePropertyMute,
            Scope = kAudioObjectPropertyScopeOutput,
            Element = 0
        };

        if (AudioObjectHasProperty(deviceId, ref muteAddress))
            AudioObjectAddPropertyListener(deviceId, ref muteAddress, _muteListener, IntPtr.Zero);

        muteAddress.Scope = kAudioObjectPropertyScopeInput;
        if (AudioObjectHasProperty(deviceId, ref muteAddress))
            AudioObjectAddPropertyListener(deviceId, ref muteAddress, _muteListener, IntPtr.Zero);

        _monitoredDeviceIds.Add(deviceId);
    }

    private void UnmonitorAllDevices()
    {
        foreach (var deviceId in _monitoredDeviceIds)
        {
            if (_volumeListener is not null)
            {
                var volAddress = new AudioObjectPropertyAddress
                {
                    Selector = kVirtualMasterVolume,
                    Scope = kAudioObjectPropertyScopeOutput,
                    Element = 0
                };
                AudioObjectRemovePropertyListener(deviceId, ref volAddress, _volumeListener, IntPtr.Zero);
                volAddress.Scope = kAudioObjectPropertyScopeInput;
                AudioObjectRemovePropertyListener(deviceId, ref volAddress, _volumeListener, IntPtr.Zero);
            }

            if (_muteListener is not null)
            {
                var muteAddress = new AudioObjectPropertyAddress
                {
                    Selector = kAudioDevicePropertyMute,
                    Scope = kAudioObjectPropertyScopeOutput,
                    Element = 0
                };
                AudioObjectRemovePropertyListener(deviceId, ref muteAddress, _muteListener, IntPtr.Zero);
                muteAddress.Scope = kAudioObjectPropertyScopeInput;
                AudioObjectRemovePropertyListener(deviceId, ref muteAddress, _muteListener, IntPtr.Zero);
            }
        }

        _monitoredDeviceIds.Clear();
    }

    // ── Private: listener callbacks ──────────────────────────────────

    private int OnDeviceListChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
        {
            DeviceName = "system",
            EventType = AudioDeviceEventType.Changed
        });
        return kAudioHardwareNoError;
    }

    private int OnVolumeChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        var uid = GetDeviceUid(objectId);
        if (uid is not null)
        {
            DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
            {
                DeviceName = uid,
                EventType = AudioDeviceEventType.Changed
            });
        }
        return kAudioHardwareNoError;
    }

    private int OnMuteChanged(uint objectId, uint numberAddresses, IntPtr addresses, IntPtr clientData)
    {
        var uid = GetDeviceUid(objectId);
        if (uid is not null)
        {
            DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
            {
                DeviceName = uid,
                EventType = AudioDeviceEventType.Changed
            });
        }
        return kAudioHardwareNoError;
    }

    // ── Private: CFString conversion ─────────────────────────────────

    private static string? CfStringToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;

        try
        {
            var length = (int)CFStringGetLength(cfString);
            if (length == 0) return string.Empty;

            // Copy UTF-16 characters to a managed buffer
            var buffer = Marshal.AllocHGlobal(length * 2);
            try
            {
                CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = length }, buffer);
                var chars = new char[length];
                Marshal.Copy(buffer, chars, 0, length);
                return new string(chars);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        try { StopMonitoringAsync().GetAwaiter().GetResult(); }
        catch { /* best effort */ }
    }
}
