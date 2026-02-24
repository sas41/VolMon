namespace VolMon.Core.Audio;

/// <summary>
/// Platform abstraction for discovering and controlling per-application audio streams
/// and hardware audio devices. Implementations exist for PulseAudio/PipeWire (Linux), etc.
/// </summary>
public interface IAudioBackend : IDisposable
{
    // ── Streams (per-application) ────────────────────────────────────

    /// <summary>Returns all currently active audio streams.</summary>
    Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default);

    /// <summary>Sets the volume for a specific stream.</summary>
    Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default);

    /// <summary>Mutes or unmutes a specific stream.</summary>
    Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default);

    // ── Devices (hardware sinks/sources) ─────────────────────────────

    /// <summary>Returns all audio devices (sinks and sources).</summary>
    Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>Sets the volume for a device by its stable name.</summary>
    Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default);

    /// <summary>Mutes or unmutes a device by its stable name.</summary>
    Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default);

    // ── Events ───────────────────────────────────────────────────────

    /// <summary>Raised when a new audio stream is created.</summary>
    event EventHandler<AudioStreamEventArgs> StreamCreated;

    /// <summary>Raised when an audio stream is removed.</summary>
    event EventHandler<AudioStreamEventArgs> StreamRemoved;

    /// <summary>Raised when an audio stream's properties change.</summary>
    event EventHandler<AudioStreamEventArgs> StreamChanged;

    /// <summary>Raised when a device is added, removed, or changed.</summary>
    event EventHandler<AudioDeviceEventArgs> DeviceChanged;

    /// <summary>
    /// Raised when the system default audio sink changes (e.g. user switches
    /// from speakers to headphones or Bluetooth). The event argument contains
    /// the new default sink name.
    /// </summary>
    event EventHandler<DefaultSinkChangedEventArgs> DefaultSinkChanged;

    // ── Monitoring ───────────────────────────────────────────────────

    /// <summary>Starts background monitoring for stream and device events.</summary>
    Task StartMonitoringAsync(CancellationToken ct = default);

    /// <summary>Stops background monitoring.</summary>
    Task StopMonitoringAsync();

    // New: report per-process information (a process with its streams)
    Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default);

    // ── CompatibilityMode: virtual sink routing ───────────────────────

    /// <summary>
    /// Creates a null-sink virtual device named <paramref name="sinkName"/> and returns
    /// the PA module index that owns it. Returns <c>null</c> if the backend does not
    /// support virtual sinks (e.g. Windows, macOS stubs).
    /// </summary>
    Task<uint?> CreateVirtualSinkAsync(string sinkName, string description,
        CancellationToken ct = default);

    /// <summary>
    /// Destroys a virtual sink previously created with
    /// <see cref="CreateVirtualSinkAsync"/>, identified by the module index
    /// returned from that call.
    /// </summary>
    Task DestroyVirtualSinkAsync(uint moduleIndex, CancellationToken ct = default);

    /// <summary>
    /// Moves a sink input (stream) to the named sink.
    /// Used to route a stream into a null-sink or back to the real hardware sink.
    /// </summary>
    Task MoveStreamToSinkAsync(string streamId, string sinkName,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the volume (0–100) on a virtual sink by its node name.
    /// No-op on backends that do not support virtual sinks.
    /// </summary>
    Task SetVirtualSinkVolumeAsync(string sinkName, int volume, CancellationToken ct = default);

    /// <summary>
    /// Sets the mute state on a virtual sink by its node name.
    /// No-op on backends that do not support virtual sinks.
    /// </summary>
    Task SetVirtualSinkMuteAsync(string sinkName, bool muted, CancellationToken ct = default);

    /// <summary>
    /// Re-links a virtual sink's monitor output ports to a different hardware sink.
    /// Called when the system default sink changes so that audio from virtual sinks
    /// flows to the new output device. <paramref name="targetSinkName"/> is the PA
    /// sink name of the new target (e.g. <c>"alsa_output.pci-0000_00_1f.3.analog-stereo"</c>).
    /// No-op on backends that do not support virtual sinks.
    /// </summary>
    Task RelinkVirtualSinkAsync(string virtualSinkName, string targetSinkName,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the name of the current default audio output sink, or <c>null</c>
    /// if it cannot be determined.
    /// </summary>
    Task<string?> GetDefaultSinkNameAsync(CancellationToken ct = default);
}

public sealed class AudioStreamEventArgs : EventArgs
{
    public required string StreamId { get; init; }
    public AudioStream? Stream { get; init; }
}

public sealed class AudioDeviceEventArgs : EventArgs
{
    public required string DeviceName { get; init; }
    public required AudioDeviceEventType EventType { get; init; }
}

public enum AudioDeviceEventType
{
    Added,
    Removed,
    Changed
}

public sealed class DefaultSinkChangedEventArgs : EventArgs
{
    /// <summary>The PA sink name of the new default output sink.</summary>
    public required string SinkName { get; init; }
}
