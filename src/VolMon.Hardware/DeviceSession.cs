using Microsoft.Extensions.Logging;
using VolMon.Core.Audio;
using VolMon.Core.Ipc;

namespace VolMon.Hardware;

/// <summary>
/// The runtime state of a device session.
/// </summary>
public enum DeviceSessionState
{
    Starting,
    Running,
    Faulted,
    Stopped
}

/// <summary>
/// An isolated session for a single hardware device. Runs the controller in its own
/// Task with crash containment — if the device throws, this session is marked faulted
/// and other devices continue running.
///
/// Handles the IPC bridge logic for this specific device: dial events → volume commands,
/// button events → mute toggles, daemon state → display updates + LED colors.
/// </summary>
internal sealed class DeviceSession : IAsyncDisposable
{
    private readonly IHardwareController _controller;
    private readonly IpcDuplexClient _ipc;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan EchoSuppressionWindow = TimeSpan.FromMilliseconds(200);
    private static readonly (byte R, byte G, byte B) ColorUnassigned = (0x20, 0x20, 0x20);

    // Per-dial state
    private readonly Guid?[] _dialGroupIds;
    private readonly int[] _dialVolumes;
    private readonly bool[] _dialMuted;
    private readonly string?[] _dialGroupNames;
    private readonly string?[] _dialGroupColors;
    private readonly string[]?[] _dialActiveMembers;
    private readonly string[]?[] _dialInactiveMembers;

    // Cached process/device data for member resolution
    private List<AudioProcessInfo>? _lastProcesses;
    private List<AudioDeviceInfo>? _lastDevices;

    // Debounce + echo suppression
    private readonly int[] _pendingDeltas;
    private readonly CancellationTokenSource?[] _debounceCts;
    private readonly object _debounceLock = new();
    private readonly DateTime[] _lastCommandTime;

    public string DeviceId => _controller.DeviceId;
    public string DeviceName => _controller.DeviceName;
    public DeviceSessionState State { get; private set; } = DeviceSessionState.Starting;
    public Exception? Fault { get; private set; }

    public DeviceSession(IHardwareController controller, IpcDuplexClient ipc, ILogger logger)
    {
        _controller = controller;
        _ipc = ipc;
        _logger = logger;

        var dialCount = controller.DialCount;
        _dialGroupIds = new Guid?[dialCount];
        _dialVolumes = new int[dialCount];
        _dialMuted = new bool[dialCount];
        _dialGroupNames = new string?[dialCount];
        _dialGroupColors = new string?[dialCount];
        _dialActiveMembers = new string[]?[dialCount];
        _dialInactiveMembers = new string[]?[dialCount];
        _pendingDeltas = new int[dialCount];
        _debounceCts = new CancellationTokenSource?[dialCount];
        _lastCommandTime = new DateTime[dialCount];
    }

    /// <summary>
    /// Start the device session. Returns immediately; the session runs in the background.
    /// </summary>
    public void Start()
    {
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Stop the device session gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        if (State == DeviceSessionState.Stopped) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch { /* already handled inside RunAsync */ }
        }
        State = DeviceSessionState.Stopped;
    }

    /// <summary>
    /// Push a state update from the daemon to this device (called by DeviceManager
    /// when a state-changed event arrives).
    /// </summary>
    public void OnDaemonStateChanged(
        List<AudioGroup> groups,
        List<AudioProcessInfo>? processes = null,
        List<AudioDeviceInfo>? devices = null)
    {
        if (State != DeviceSessionState.Running) return;

        // Cache process/device data for member resolution
        if (processes is not null) _lastProcesses = processes;
        if (devices is not null) _lastDevices = devices;

        try
        {
            ApplyGroupState(groups);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Device}] Failed to apply state update", DeviceId);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Start the hardware controller
            await _controller.StartAsync(ct);

            if (!_controller.IsConnected)
            {
                _logger.LogWarning("[{Device}] Device did not connect", DeviceId);
                State = DeviceSessionState.Stopped;
                return;
            }

            // Wire events
            _controller.DialRotated += OnDialRotated;
            _controller.ButtonPressed += OnButtonPressed;

            State = DeviceSessionState.Running;
            _logger.LogInformation("[{Device}] Session started", DeviceId);

            // Fetch initial state from daemon
            await FetchInitialStateAsync(ct);

            // Idle until cancelled
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Fault = ex;
            State = DeviceSessionState.Faulted;
            _logger.LogError(ex, "[{Device}] Session faulted", DeviceId);
            return;
        }
        finally
        {
            _controller.DialRotated -= OnDialRotated;
            _controller.ButtonPressed -= OnButtonPressed;

            try { await _controller.StopAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Device}] Error during controller stop", DeviceId);
            }
        }

        State = DeviceSessionState.Stopped;
    }

    // ── Initial state fetch ─────────────────────────────────────────

    private async Task FetchInitialStateAsync(CancellationToken ct)
    {
        try
        {
            // Use "status" instead of "list-groups" to also get process/device data
            // for resolving active group members on startup.
            var resp = await _ipc.SendAsync(new IpcRequest { Command = "status" }, ct);
            if (resp.Success && resp.Groups is not null)
            {
                if (resp.Processes is not null) _lastProcesses = resp.Processes;
                if (resp.Devices is not null) _lastDevices = resp.Devices;
                ApplyGroupState(resp.Groups);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Device}] Failed to fetch initial state", DeviceId);
        }
    }

    // ── Dial rotation → volume ──────────────────────────────────────

    private void OnDialRotated(object? sender, DialRotatedEventArgs e)
    {
        if (e.DialIndex < 0 || e.DialIndex >= _dialGroupIds.Length) return;
        if (_dialGroupIds[e.DialIndex] is null) return;

        lock (_debounceLock)
        {
            _pendingDeltas[e.DialIndex] += e.Delta;

            // Cancel any existing debounce timer for this dial
            _debounceCts[e.DialIndex]?.Cancel();
            _debounceCts[e.DialIndex]?.Dispose();

            var cts = new CancellationTokenSource();
            _debounceCts[e.DialIndex] = cts;

            _ = FlushAfterDebounceAsync(e.DialIndex, cts.Token);
        }
    }

    private async Task FlushAfterDebounceAsync(int dialIndex, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer debounce
        }

        int accumulatedDelta;
        lock (_debounceLock)
        {
            accumulatedDelta = _pendingDeltas[dialIndex];
            _pendingDeltas[dialIndex] = 0;
        }

        if (accumulatedDelta == 0) return;

        var groupId = _dialGroupIds[dialIndex];
        if (groupId is null) return;

        var step = _controller.VolumeStepPerDelta;
        var currentVol = _dialVolumes[dialIndex];

        // Apply knob acceleration: scale the effective step based on rotation speed
        var effectiveStep = ApplyAcceleration(accumulatedDelta, step);
        var direction = Math.Sign(accumulatedDelta);
        var newVol = Math.Clamp(currentVol + direction * effectiveStep, 0, 100);

        _dialVolumes[dialIndex] = newVol;
        _lastCommandTime[dialIndex] = DateTime.UtcNow;

        try
        {
            await _ipc.SendFireAndForgetAsync(new IpcRequest
            {
                Command = "set-group-volume",
                GroupId = groupId.Value,
                Volume = newVol
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Device}] Failed to send volume for dial {Dial}", DeviceId, dialIndex);
        }

        PushDisplayUpdate();
    }

    // ── Button press → mute toggle ──────────────────────────────────

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!e.Pressed) return; // Only act on press-down
        if (e.ButtonIndex < 0 || e.ButtonIndex >= _dialGroupIds.Length) return;

        var groupId = _dialGroupIds[e.ButtonIndex];
        if (groupId is null) return;

        var wasMuted = _dialMuted[e.ButtonIndex];
        _dialMuted[e.ButtonIndex] = !wasMuted;
        _lastCommandTime[e.ButtonIndex] = DateTime.UtcNow;

        var command = wasMuted ? "unmute-group" : "mute-group";
        _ = SendMuteCommandAsync(groupId.Value, command, e.ButtonIndex);

        UpdateDialColor(e.ButtonIndex);
        PushDisplayUpdate();
    }

    private async Task SendMuteCommandAsync(Guid groupId, string command, int dialIndex)
    {
        try
        {
            await _ipc.SendFireAndForgetAsync(new IpcRequest
            {
                Command = command,
                GroupId = groupId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Device}] Failed to send {Command} for dial {Dial}",
                DeviceId, command, dialIndex);
        }
    }

    // ── State application ───────────────────────────────────────────

    private void ApplyGroupState(List<AudioGroup> groups)
    {
        for (var i = 0; i < _dialGroupIds.Length; i++)
        {
            if (i < groups.Count)
            {
                var g = groups[i];

                // Echo suppression: skip daemon updates for dials we recently commanded
                if ((DateTime.UtcNow - _lastCommandTime[i]) < EchoSuppressionWindow)
                    continue;

                _dialGroupIds[i] = g.Id;
                _dialVolumes[i] = g.Volume;
                _dialMuted[i] = g.Muted;
                _dialGroupNames[i] = g.Name;
                _dialGroupColors[i] = g.Color;

                // Resolve active/inactive members for this group
                ResolveGroupMembers(g, i);
            }
            else
            {
                _dialGroupIds[i] = null;
                _dialVolumes[i] = 0;
                _dialMuted[i] = false;
                _dialGroupNames[i] = null;
                _dialGroupColors[i] = null;
                _dialActiveMembers[i] = null;
                _dialInactiveMembers[i] = null;
            }

            UpdateDialColor(i);
        }

        PushDisplayUpdate();
    }

    /// <summary>
    /// Determine which configured programs/devices in a group are currently running
    /// vs not running. A process is considered "active" if it appears in the daemon's
    /// process snapshot (either with audio streams assigned to this group, or running
    /// but silent). This matches the GUI behavior where running-but-silent processes
    /// show a green status dot. Processes not in the snapshot at all are "inactive."
    /// </summary>
    private void ResolveGroupMembers(AudioGroup group, int dialIndex)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add all configured programs and devices
        foreach (var prog in group.Programs)
            configured.Add(prog);
        foreach (var dev in group.Devices)
            configured.Add(dev);

        // Find active members from process snapshot.
        // A process is active if:
        //   1. It has streams assigned to this group (producing audio), OR
        //   2. It's running but silent AND is configured in this group
        //      (daemon only sends silent processes if they're configured)
        if (_lastProcesses is not null)
        {
            foreach (var proc in _lastProcesses)
            {
                var hasStreamInGroup = false;
                foreach (var stream in proc.Streams)
                {
                    if (stream.AssignedGroup == group.Id)
                    {
                        hasStreamInGroup = true;
                        break;
                    }
                }

                if (hasStreamInGroup)
                {
                    active.Add(proc.Name);
                }
                else if (proc.Streams.Count == 0 && configured.Contains(proc.Name))
                {
                    // Running but silent — treat as active (matches GUI behavior)
                    active.Add(proc.Name);
                }
            }
        }

        // Find active devices from device snapshot
        if (_lastDevices is not null)
        {
            foreach (var dev in _lastDevices)
            {
                if (dev.AssignedGroup == group.Id)
                    active.Add(dev.Description ?? dev.Name);
            }
        }

        _dialActiveMembers[dialIndex] = active.Count > 0 ? [.. active] : [];
        _dialInactiveMembers[dialIndex] = configured.Except(active, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ── Knob acceleration ─────────────────────────────────────────────

    /// <summary>
    /// Apply acceleration curve to the accumulated delta. When the knob is turned
    /// faster (larger accumulated delta within the debounce window), the effective
    /// volume step is scaled up.
    ///
    /// Returns the absolute step size (always positive) to apply in the direction
    /// of the accumulated delta.
    /// </summary>
    private int ApplyAcceleration(int accumulatedDelta, int baseStep)
    {
        if (!_controller.KnobAcceleration)
            return Math.Abs(accumulatedDelta) * baseStep;

        var absDelta = Math.Abs(accumulatedDelta);
        var threshold = _controller.AccelerationThreshold;
        var maxMultiplier = _controller.AccelerationMaxMultiplier;
        var saturation = _controller.AccelerationSaturation;

        if (absDelta <= threshold)
        {
            // Below threshold: linear, no acceleration
            return absDelta * baseStep;
        }

        // Above threshold: scale the excess portion with an acceleration multiplier.
        // The multiplier ramps linearly from 1x at threshold to maxMultiplier at saturation.
        var excess = absDelta - threshold;
        var range = Math.Max(saturation - threshold, 1);
        var t = Math.Min((float)excess / range, 1f);
        var multiplier = 1f + t * (maxMultiplier - 1f);

        // Base portion (below threshold) + accelerated excess
        var result = threshold * baseStep + (int)Math.Ceiling(excess * baseStep * multiplier);
        return result;
    }

    // ── Display ─────────────────────────────────────────────────────

    private void PushDisplayUpdate()
    {
        if (!_controller.HasDisplay) return;

        var states = new GroupDisplayState[_dialGroupIds.Length];
        for (var i = 0; i < states.Length; i++)
        {
            states[i] = new GroupDisplayState
            {
                Name = _dialGroupNames[i] ?? $"Dial {i + 1}",
                Volume = _dialVolumes[i],
                Muted = _dialMuted[i],
                Color = _dialGroupColors[i],
                ActiveMembers = _dialActiveMembers[i] ?? [],
                InactiveMembers = _dialInactiveMembers[i] ?? []
            };
        }

        _controller.UpdateDisplay(states);
    }

    // ── LED colors ──────────────────────────────────────────────────

    private void UpdateDialColor(int dialIndex)
    {
        if (_dialGroupIds[dialIndex] is null)
        {
            _ = _controller.SetDialColorAsync(dialIndex,
                ColorUnassigned.R, ColorUnassigned.G, ColorUnassigned.B);
            return;
        }

        if (_dialMuted[dialIndex])
        {
            _ = _controller.SetDialColorAsync(dialIndex, 0xE6, 0x39, 0x46); // Red
            return;
        }

        var hex = _dialGroupColors[dialIndex];
        if (TryParseHexColor(hex, out var r, out var g, out var b))
        {
            _ = _controller.SetDialColorAsync(dialIndex, r, g, b);
        }
        else
        {
            _ = _controller.SetDialColorAsync(dialIndex, 0xFF, 0xFF, 0xFF);
        }
    }

    private static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex)) return false;

        var span = hex.AsSpan();
        if (span[0] == '#') span = span[1..];

        if (span.Length != 6) return false;
        if (!byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out r)) return false;
        if (!byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out g)) return false;
        if (!byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out b)) return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        await _controller.DisposeAsync();
    }
}
