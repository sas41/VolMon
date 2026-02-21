namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Stub Windows audio backend. Will use Windows Core Audio API (IAudioSessionManager2)
/// via COM interop or the NAudio NuGet package.
/// </summary>
public sealed class WindowsAudioBackend : IAudioBackend
{
    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task StartMonitoringAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public Task StopMonitoringAsync() =>
        throw new PlatformNotSupportedException("Windows audio backend is not yet implemented.");

    public void Dispose() { }
}
