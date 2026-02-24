using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VolMon.Core.Audio;
using VolMon.Core.Ipc;
using VolMon.Hardware.Beacn.Mix;

namespace VolMon.Hardware;

/// <summary>
/// Background service that bridges hardware controller events to the VolMon daemon via IPC.
///
/// Dial rotation    -> set-group-volume (debounced, delta accumulated)
/// Dial button      -> mute-group / unmute-group (toggle on press)
///
/// Listens to daemon state-changed events to:
/// - Track current group volumes and mute states
/// - Update dial LED colors based on group color
/// - Update the device display with group info
///
/// Anti-rubber-banding:
/// - Dial deltas are accumulated and flushed after 30ms of quiet
/// - After sending a command, daemon echo-back is suppressed for 200ms per dial
/// </summary>
internal sealed class HardwareBridgeService : BackgroundService
{
    private readonly IHardwareController _controller;
    private readonly ILogger<HardwareBridgeService> _logger;
    private IpcDuplexClient? _ipc;

    /// <summary>How long to wait between daemon reconnection attempts.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait between device reconnection attempts.</summary>
    private static readonly TimeSpan DeviceRetryDelay = TimeSpan.FromSeconds(5);

    // ── Debounce / echo suppression constants ───────────────────────

    /// <summary>Accumulate dial deltas for this long before sending to daemon.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(30);

    /// <summary>After sending a command, ignore daemon updates for this dial for this long.</summary>
    private static readonly TimeSpan EchoSuppressionWindow = TimeSpan.FromMilliseconds(200);

    // ── Cached group state (updated from daemon events) ─────────────

    private readonly Guid?[] _dialGroupIds = new Guid?[4];
    private readonly int[] _dialVolumes = new int[4];
    private readonly bool[] _dialMuted = new bool[4];
    private readonly string?[] _dialGroupNames = new string?[4];
    private readonly string?[] _dialGroupColors = new string?[4];

    // ── Debounce state per dial ─────────────────────────────────────

    private readonly int[] _pendingDeltas = new int[4];
    private readonly CancellationTokenSource?[] _debounceCts = new CancellationTokenSource?[4];
    private readonly object _debounceLock = new();

    // ── Echo suppression timestamps per dial ────────────────────────

    private readonly DateTime[] _lastCommandTime = new DateTime[4];

    // ── LED colors ──────────────────────────────────────────────────

    private static readonly (byte R, byte G, byte B) ColorUnassigned = (0x20, 0x20, 0x20);

    public HardwareBridgeService(
        IHardwareController controller,
        ILogger<HardwareBridgeService> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Phase 1: Connect to the hardware device (retry until found)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _controller.StartAsync(stoppingToken);

                if (_controller.IsConnected)
                    break;

                _logger.LogInformation(
                    "Beacn Mix not found. Retrying in {Delay}s...",
                    DeviceRetryDelay.TotalSeconds);
                await Task.Delay(DeviceRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to hardware device");
                await Task.Delay(DeviceRetryDelay, stoppingToken);
            }
        }

        // Phase 2: Connect to the daemon via IPC (retry until connected)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ipc = new IpcDuplexClient();
                await _ipc.ConnectAsync(TimeSpan.FromSeconds(5), stoppingToken);

                _logger.LogInformation("Connected to VolMon daemon via IPC.");
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Daemon not available yet, retrying...");
                if (_ipc is not null)
                {
                    await _ipc.DisposeAsync();
                    _ipc = null;
                }
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }

        if (_ipc is null || stoppingToken.IsCancellationRequested)
            return;

        // Phase 3: Wire up events and fetch initial state
        _controller.DialRotated += OnDialRotated;
        _controller.ButtonPressed += OnButtonPressed;
        _ipc.EventReceived += OnDaemonEvent;
        _ipc.Disconnected += OnDaemonDisconnected;

        // Fetch initial state
        await RefreshGroupStateAsync(stoppingToken);

        // Phase 4: Idle until stopped
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _controller.DialRotated -= OnDialRotated;
            _controller.ButtonPressed -= OnButtonPressed;

            if (_ipc is not null)
            {
                _ipc.EventReceived -= OnDaemonEvent;
                _ipc.Disconnected -= OnDaemonDisconnected;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _controller.StopAsync();

        if (_ipc is not null)
            await _ipc.DisposeAsync();
    }

    // ── Event handlers ──────────────────────────────────────────────

    private int GetVolumeStep()
    {
        if (_controller is BeacnMixController mix)
            return mix.VolumeStepPerDelta;
        return 1;
    }

    private void OnDialRotated(object? sender, DialRotatedEventArgs e)
    {
        if (_ipc is null || !_ipc.IsConnected) return;
        if (_dialGroupIds[e.DialIndex] is null) return;

        lock (_debounceLock)
        {
            // Accumulate delta
            _pendingDeltas[e.DialIndex] += e.Delta;

            // Apply locally immediately for responsive display
            var step = GetVolumeStep();
            var newVolume = Math.Clamp(_dialVolumes[e.DialIndex] + (e.Delta * step), 0, 100);
            _dialVolumes[e.DialIndex] = newVolume;

            // Cancel previous debounce timer for this dial
            _debounceCts[e.DialIndex]?.Cancel();
            _debounceCts[e.DialIndex]?.Dispose();

            var cts = new CancellationTokenSource();
            _debounceCts[e.DialIndex] = cts;

            var dialIndex = e.DialIndex;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceWindow, cts.Token);
                    await FlushDialAsync(dialIndex);
                }
                catch (OperationCanceledException) { /* superseded by newer rotation */ }
            });
        }

        // Update display immediately (no debounce on visuals)
        PushDisplayUpdate();
    }

    private async Task FlushDialAsync(int dialIndex)
    {
        int totalDelta;
        Guid? groupId;

        lock (_debounceLock)
        {
            totalDelta = _pendingDeltas[dialIndex];
            _pendingDeltas[dialIndex] = 0;
            groupId = _dialGroupIds[dialIndex];
        }

        if (totalDelta == 0 || groupId is null) return;

        // Mark echo suppression
        _lastCommandTime[dialIndex] = DateTime.UtcNow;

        var volume = _dialVolumes[dialIndex];

        try
        {
            await _ipc!.SendFireAndForgetAsync(new IpcRequest
            {
                Command = "set-group-volume",
                GroupId = groupId.Value,
                Volume = volume
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send volume command for dial {Index}", dialIndex);
        }
    }

    private async void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!e.Pressed) return;
        if (_ipc is null || !_ipc.IsConnected) return;

        var groupId = _dialGroupIds[e.ButtonIndex];
        if (groupId is null) return;

        var currentlyMuted = _dialMuted[e.ButtonIndex];
        var command = currentlyMuted ? "unmute-group" : "mute-group";

        _dialMuted[e.ButtonIndex] = !currentlyMuted;

        // Mark echo suppression
        _lastCommandTime[e.ButtonIndex] = DateTime.UtcNow;

        // Update display and LEDs immediately
        var ledColor = GetLedColor(_dialGroupColors[e.ButtonIndex], !currentlyMuted, e.ButtonIndex);
        _ = _controller.SetDialColorAsync(e.ButtonIndex, ledColor.R, ledColor.G, ledColor.B);
        PushDisplayUpdate();

        try
        {
            await _ipc.SendFireAndForgetAsync(new IpcRequest
            {
                Command = command,
                GroupId = groupId.Value
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send mute command for button {Index}", e.ButtonIndex);
        }
    }

    private void OnDaemonEvent(object? sender, IpcEvent e)
    {
        if (e.Name == "state-changed" && e.Groups is not null)
        {
            UpdateGroupState(e.Groups);
        }
    }

    private void OnDaemonDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("Lost connection to daemon.");
    }

    // ── State management ────────────────────────────────────────────

    private async Task RefreshGroupStateAsync(CancellationToken ct)
    {
        if (_ipc is null) return;

        try
        {
            var response = await _ipc.SendAsync(new IpcRequest { Command = "list-groups" }, ct);
            if (response.Success && response.Groups is not null)
            {
                UpdateGroupState(response.Groups);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch initial group state");
        }
    }

    private void UpdateGroupState(List<AudioGroup> groups)
    {
        var now = DateTime.UtcNow;
        var displayChanged = false;

        for (var i = 0; i < 4; i++)
        {
            if (i < groups.Count)
            {
                var group = groups[i];
                var suppressed = (now - _lastCommandTime[i]) < EchoSuppressionWindow;

                _dialGroupIds[i] = group.Id;
                _dialGroupNames[i] = group.Name;
                _dialGroupColors[i] = group.Color;

                if (!suppressed)
                {
                    // Accept daemon values only when we're not actively controlling this dial
                    _dialVolumes[i] = group.Volume;
                    _dialMuted[i] = group.Muted;
                    displayChanged = true;
                }

                // Always update LED (color might have changed even if volume is suppressed)
                var ledColor = GetLedColor(group.Color, _dialMuted[i], i);
                _ = _controller.SetDialColorAsync(i, ledColor.R, ledColor.G, ledColor.B);
            }
            else
            {
                _dialGroupIds[i] = null;
                _dialVolumes[i] = 0;
                _dialMuted[i] = false;
                _dialGroupNames[i] = null;
                _dialGroupColors[i] = null;
                displayChanged = true;

                _ = _controller.SetDialColorAsync(i, ColorUnassigned.R, ColorUnassigned.G, ColorUnassigned.B);
            }
        }

        if (displayChanged)
            PushDisplayUpdate();

        _logger.LogDebug(
            "Group state updated: {Groups}",
            string.Join(", ", groups.Take(4).Select((g, i) =>
                $"Dial{i}={g.Name}({g.Volume}%{(g.Muted ? " MUTED" : "")})")));
    }

    private void PushDisplayUpdate()
    {
        if (_controller is not BeacnMixController mix) return;

        var states = new GroupDisplayState[4];
        for (var i = 0; i < 4; i++)
        {
            states[i] = new GroupDisplayState
            {
                Name = _dialGroupNames[i] ?? $"Dial {i + 1}",
                Volume = _dialVolumes[i],
                Muted = _dialMuted[i],
                Color = _dialGroupColors[i]
            };
        }

        mix.UpdateDisplay(states);
    }

    /// <summary>
    /// Parse the group's hex color and return an LED color.
    /// When muted, returns red. When unmuted, returns the group color (or a default).
    /// </summary>
    private static (byte R, byte G, byte B) GetLedColor(string? hexColor, bool muted, int index)
    {
        if (muted)
            return (0xFF, 0x00, 0x00); // Red for muted

        if (!string.IsNullOrEmpty(hexColor) && hexColor.StartsWith('#') && hexColor.Length >= 7)
        {
            try
            {
                var r = Convert.ToByte(hexColor.Substring(1, 2), 16);
                var g = Convert.ToByte(hexColor.Substring(3, 2), 16);
                var b = Convert.ToByte(hexColor.Substring(5, 2), 16);
                return (r, g, b);
            }
            catch { }
        }

        return index switch
        {
            0 => (0xFF, 0x95, 0x00),
            1 => (0x00, 0xB4, 0xD8),
            2 => (0xE6, 0x39, 0x46),
            3 => (0x2E, 0xC4, 0xB6),
            _ => (0x4A, 0x90, 0xD9)
        };
    }
}
