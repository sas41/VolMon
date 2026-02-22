using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VolMon.Core.Audio;

namespace VolMon.Core.Audio.Backends;

// Simple per-stream as-per-process mapping for macOS (Plan A backfill).
public sealed class MacOsAudioBackend : IAudioBackend
{
    public event System.EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event System.EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event System.EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event System.EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    public Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<AudioStream>)new List<AudioStream>());

    public Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<AudioDevice>)new List<AudioDevice>());

    public Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default) => Task.CompletedTask;

    public Task StartMonitoringAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopMonitoringAsync() => Task.CompletedTask;

    public void Dispose() { }

    public Task<IReadOnlyList<AudioProcess>> GetProcessesAsync(CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<AudioProcess>)new List<AudioProcess>());
}
