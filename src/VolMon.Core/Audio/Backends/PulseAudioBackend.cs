using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VolMon.Core.Audio;

namespace VolMon.Core.Audio.Backends;

// Minimal stub PulsedAudio backend to restore compilation.
public sealed class PulseAudioBackend : IAudioBackend
{
    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<AudioStream>)new List<AudioStream>());

    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<AudioDevice>)new List<AudioDevice>());

    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default) => Task.CompletedTask;

    public Task StartMonitoringAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopMonitoringAsync() => Task.CompletedTask;

    public void Dispose() { }

    public Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<AudioProcess>)new List<AudioProcess>());
}
