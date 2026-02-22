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

    // ── Monitoring ───────────────────────────────────────────────────

    /// <summary>Starts background monitoring for stream and device events.</summary>
    Task StartMonitoringAsync(CancellationToken ct = default);

    /// <summary>Stops background monitoring.</summary>
    Task StopMonitoringAsync();

    // New: report per-process information (a process with its streams)
    Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default);
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
