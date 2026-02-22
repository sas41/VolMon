using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VolMon.Core.Audio.Backends;

/// <summary>
/// Linux audio backend using pactl (works with both PulseAudio and PipeWire).
/// Spawns CLI processes and parses their output — no native library bindings needed.
/// </summary>
public sealed partial class PulseAudioLegacyBackend : IAudioBackend
{
    private Process? _subscribeProcess;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    /// <summary>Cache for dynamically-built property regex patterns.</summary>
    private static readonly ConcurrentDictionary<string, Regex> PropertyRegexCache = new();

    public event EventHandler<AudioStreamEventArgs>? StreamCreated;
    public event EventHandler<AudioStreamEventArgs>? StreamRemoved;
    public event EventHandler<AudioStreamEventArgs>? StreamChanged;
    public event EventHandler<AudioDeviceEventArgs>? DeviceChanged;

    // ── Streams ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AudioStream>> GetStreamsAsync(CancellationToken ct = default)
    {
        var output = await RunPactlAsync("list sink-inputs", ct);
        return ParseSinkInputs(output);
    }

    /// <inheritdoc/>
    public async Task SetStreamVolumeAsync(string streamId, int volume, CancellationToken ct = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        await RunPactlAsync($"set-sink-input-volume {streamId} {volume}%", ct);
    }

    /// <inheritdoc/>
    public async Task SetStreamMuteAsync(string streamId, bool muted, CancellationToken ct = default)
    {
        var muteStr = muted ? "1" : "0";
        await RunPactlAsync($"set-sink-input-mute {streamId} {muteStr}", ct);
    }

    // ── Devices ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<AudioDevice>();

        var sinksOutput = await RunPactlAsync("list sinks", ct);
        devices.AddRange(ParseDevices(sinksOutput, DeviceType.Sink));

        var sourcesOutput = await RunPactlAsync("list sources", ct);
        devices.AddRange(ParseDevices(sourcesOutput, DeviceType.Source));

        return devices;
    }

    /// <inheritdoc/>
    public async Task SetDeviceVolumeAsync(string deviceName, int volume, CancellationToken ct = default)
    {
        volume = Math.Clamp(volume, 0, 100);

        // Try as sink first, then as source. One will succeed.
        try
        {
            await RunPactlAsync($"set-sink-volume {EscapeArg(deviceName)} {volume}%", ct);
            return;
        }
        catch { /* not a sink, try source */ }

        await RunPactlAsync($"set-source-volume {EscapeArg(deviceName)} {volume}%", ct);
    }

    /// <inheritdoc/>
    public async Task SetDeviceMuteAsync(string deviceName, bool muted, CancellationToken ct = default)
    {
        var muteStr = muted ? "1" : "0";

        try
        {
            await RunPactlAsync($"set-sink-mute {EscapeArg(deviceName)} {muteStr}", ct);
            return;
        }
        catch { /* not a sink, try source */ }

        await RunPactlAsync($"set-source-mute {EscapeArg(deviceName)} {muteStr}", ct);
    }

    // ── Monitoring ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task StartMonitoringAsync(CancellationToken ct = default)
    {
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopMonitoringAsync()
    {
        _monitorCts?.Cancel();

        if (_subscribeProcess is { HasExited: false })
        {
            try { _subscribeProcess.Kill(); }
            catch { /* already exited */ }
        }

        if (_monitorTask is not null)
        {
            try { await _monitorTask; }
            catch (OperationCanceledException) { }
        }
    }

    // ── Private: monitoring loop ─────────────────────────────────────

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _subscribeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pactl",
                        Arguments = "subscribe",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                _subscribeProcess.Start();

                using var reader = _subscribeProcess.StandardOutput;
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    ProcessSubscribeEvent(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // pactl subscribe crashed or disconnected; retry after delay
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Parses a single line from `pactl subscribe` output.
    /// Examples:
    ///   Event 'new' on sink-input #42
    ///   Event 'remove' on sink-input #42
    ///   Event 'change' on sink #0
    ///   Event 'new' on source #3
    /// </summary>
    private void ProcessSubscribeEvent(string line)
    {
        var match = SubscribeEventRegex().Match(line);
        if (!match.Success) return;

        var eventType = match.Groups["event"].Value;
        var objectType = match.Groups["type"].Value;
        var id = match.Groups["id"].Value;

        switch (objectType)
        {
            case "sink-input":
                var streamArgs = new AudioStreamEventArgs { StreamId = id };
                switch (eventType)
                {
                    case "new": StreamCreated?.Invoke(this, streamArgs); break;
                    case "remove": StreamRemoved?.Invoke(this, streamArgs); break;
                    case "change": StreamChanged?.Invoke(this, streamArgs); break;
                }
                break;

            case "sink" or "source":
                // For device events we pass the ID; the watcher will resolve the name
                var devEventType = eventType switch
                {
                    "new" => AudioDeviceEventType.Added,
                    "remove" => AudioDeviceEventType.Removed,
                    _ => AudioDeviceEventType.Changed
                };
                DeviceChanged?.Invoke(this, new AudioDeviceEventArgs
                {
                    DeviceName = id, // numeric index; watcher resolves to name
                    EventType = devEventType
                });
                break;
        }
    }

    // ── Private: parsing ─────────────────────────────────────────────

    private static List<AudioStream> ParseSinkInputs(string output)
    {
        var streams = new List<AudioStream>();
        var blocks = output.Split("Sink Input #", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var idMatch = LeadingDigitsRegex().Match(block);
            if (!idMatch.Success) continue;

            var id = idMatch.Groups[1].Value;
            var volume = ParseVolume(block);
            var muted = ParseMuted(block);
            var appClass = ParseProperty(block, "application.name");
            var pidStr = ParseProperty(block, "application.process.id");
            int? pid = pidStr is not null && int.TryParse(pidStr, out var p) ? p : null;

            // Resolve binary name: /proc/<pid>/exe is ground truth from the kernel,
            // then fall back to PulseAudio properties, then application.name.
            var binaryName = ResolveProcessBinary(pid)
                ?? ParseProperty(block, "application.process.binary")
                ?? appClass
                ?? "unknown";

            streams.Add(new AudioStream
            {
                Id = id,
                BinaryName = binaryName,
                ApplicationClass = appClass,
                Volume = volume,
                Muted = muted,
                ProcessId = pid
            });
        }

        return streams;
    }

    /// <summary>
    /// Resolves the actual executable name from /proc/&lt;pid&gt;/exe.
    /// Returns just the filename (e.g. "firefox"), not the full path.
    /// Returns null if the PID is unavailable or the symlink can't be read.
    /// </summary>
    private static string? ResolveProcessBinary(int? pid)
    {
        if (pid is null) return null;
        try
        {
            var exePath = File.ReadAllText($"/proc/{pid}/comm").Trim();
            if (!string.IsNullOrEmpty(exePath))
                return exePath;
        }
        catch { /* process may have exited, or permission denied */ }

        try
        {
            // /proc/<pid>/exe is a symlink to the actual binary
            var target = Path.GetFileName(File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName ?? "");
            if (!string.IsNullOrEmpty(target))
                return target;
        }
        catch { /* process may have exited, or permission denied */ }

        return null;
    }

    private static List<AudioDevice> ParseDevices(string output, DeviceType type)
    {
        var devices = new List<AudioDevice>();
        var splitToken = type == DeviceType.Sink ? "Sink #" : "Source #";
        var blocks = output.Split(splitToken, StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var idMatch = LeadingDigitsRegex().Match(block);
            if (!idMatch.Success) continue;

            var id = idMatch.Groups[1].Value;

            // Name is on a line like "	Name: alsa_output.pci-0000_00_1f.3.analog-stereo"
            var nameMatch = DeviceNameRegex().Match(block);
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value.Trim();

            // Skip monitor sources (they mirror sinks, not real hardware)
            if (type == DeviceType.Source && name.Contains(".monitor"))
                continue;

            var descMatch = DeviceDescriptionRegex().Match(block);
            var description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null;

            var volume = ParseVolume(block);
            var muted = ParseMuted(block);

            devices.Add(new AudioDevice
            {
                Id = id,
                Name = name,
                Description = description,
                Type = type,
                Volume = volume,
                Muted = muted
            });
        }

        return devices;
    }

    private static int ParseVolume(string block)
    {
        var match = VolumeRegex().Match(block);
        if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var vol))
            return vol;
        return 100;
    }

    private static bool ParseMuted(string block)
    {
        var match = MuteRegex().Match(block);
        return match.Success && match.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParseProperty(string block, string propertyName)
    {
        var regex = PropertyRegexCache.GetOrAdd(propertyName, static name =>
            new Regex($@"{Regex.Escape(name)}\s*=\s*""([^""]*)""", RegexOptions.Compiled));
        var match = regex.Match(block);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Private: helpers ─────────────────────────────────────────────

    /// <summary>
    /// Quotes a pactl argument that may contain special characters (device names).
    /// </summary>
    private static string EscapeArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static async Task<string> RunPactlAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"pactl {arguments} failed (exit {process.ExitCode}): {error.Trim()}");
        }

        return output;
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        if (_subscribeProcess is { HasExited: false })
        {
            try { _subscribeProcess.Kill(); }
            catch { /* already exited */ }
        }

        _subscribeProcess?.Dispose();
    }

    [GeneratedRegex(@"Event '(?<event>\w+)' on (?<type>[\w-]+) #(?<id>\d+)")]
    private static partial Regex SubscribeEventRegex();

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex LeadingDigitsRegex();

    [GeneratedRegex(@"^\s*Name:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DeviceNameRegex();

    [GeneratedRegex(@"^\s*Description:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DeviceDescriptionRegex();

    [GeneratedRegex(@"Volume:.*?(\d+)%")]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"Mute:\s*(yes|no)", RegexOptions.IgnoreCase)]
    private static partial Regex MuteRegex();
}
