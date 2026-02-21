namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Stub macOS audio backend. Lowest priority.
/// Will use CoreAudio CLI tools when implemented.
/// </summary>
public sealed class MacOsAudioBackend : IAudioBackend
{
    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task StartMonitoringAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public Task StopMonitoringAsync() =>
        throw new PlatformNotSupportedException("macOS audio backend is not yet implemented.");

    public void Dispose() { }
}
