using Microsoft.Extensions.Logging;
using VolMon.Hardware.Beacn.Mix.Display;

namespace VolMon.Hardware.Beacn.Mix;

/// <summary>
/// IHardwareController implementation for the Beacn Mix.
/// Manages the USB device lifecycle, input polling, display rendering,
/// and display dimming/sleep.
/// </summary>
internal sealed class BeacnMixController : IHardwareController
{
    private readonly ILogger<BeacnMixController> _logger;
    private readonly BeacnMixDevice _device = new();
    private BeacnMixConfig _config = new();
    private volatile DisplayLayout _layout = DefaultLayout.Create();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _displayTimerTask;
    private ushort _lastButtons;
    private bool _disposed;

    // ── Config file watcher ─────────────────────────────────────────

    private FileSystemWatcher? _configWatcher;

    // ── Display state machine ───────────────────────────────────────

    private enum DisplayState { Active, Dimmed, Off }
    private DisplayState _displayState = DisplayState.Active;
    private DateTime _lastInputTime = DateTime.UtcNow;
    private readonly object _displayLock = new();

    // ── Cached group state for rendering ────────────────────────────

    private GroupDisplayState[] _groupStates = [];
    private readonly ManualResetEventSlim _displayDirtySignal = new(false);

    public event EventHandler<DialRotatedEventArgs>? DialRotated;
    public event EventHandler<ButtonPressedEventArgs>? ButtonPressed;

    public string DeviceId { get; private set; } = "";
    public string DeviceName => "Beacn Mix";
    public int DialCount => 4;
    public bool IsConnected => _device.IsOpen;
    public int VolumeStepPerDelta => _config.VolumeStepPerDelta;
    public bool HasDisplay => true;

    /// <summary>
    /// Create a controller for a specific Beacn Mix device.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="targetSerial">
    /// Serial number of the specific device to connect to.
    /// If null, connects to the first available Beacn Mix.
    /// </param>
    public BeacnMixController(ILogger<BeacnMixController> logger, string? targetSerial = null)
    {
        _logger = logger;
        _targetSerial = targetSerial;
    }

    private readonly string? _targetSerial;

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Looking for Beacn Mix device...");

        if (!_device.TryOpen(_targetSerial))
        {
            _logger.LogWarning("Beacn Mix not found (serial={Serial}). Will retry on next start.",
                _targetSerial ?? "any");
            return;
        }

        DeviceId = $"beacn-mix-{_device.SerialNumber}";

        _logger.LogInformation(
            "Connected to Beacn Mix (FW {Version}, Serial {Serial})",
            _device.FirmwareVersion, _device.SerialNumber);

        // Load per-device config and layout
        _config = await BeacnMixConfig.LoadAsync(_device.SerialNumber);
        _layout = await DisplayLayout.LoadAsync(_config.Layout);
        _logger.LogInformation(
            "Config: brightness={Brightness}%, dim after {Dim}s, off after {Off}s",
            _config.DisplayBrightness, _config.DimTimeoutSeconds, _config.OffTimeoutSeconds);

        var availableLayouts = DisplayLayout.ListBundledLayouts();
        _logger.LogInformation("Layout: {Layout} (available: {Available})",
            _config.Layout, string.Join(", ", availableLayouts));

        _device.Initialize(_config);

        _logger.LogInformation(
            "Input mode: {Mode}",
            _device.UsePollMode ? "poll" : "notify");

        // Watch the device config file for changes (layout switching, brightness, etc.)
        StartConfigWatcher();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lastInputTime = DateTime.UtcNow;
        _displayState = DisplayState.Active;

        // Start the input polling loop
        _pollTask = Task.Run(() => PollLoop(_cts.Token), _cts.Token);

        // Start the display timer loop (handles dim/off/wake + keep-alive)
        _displayTimerTask = Task.Run(() => DisplayTimerLoop(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        StopConfigWatcher();

        if (_cts is not null)
        {
            await _cts.CancelAsync();

            if (_pollTask is not null)
            {
                try { await _pollTask; }
                catch (OperationCanceledException) { }
            }

            if (_displayTimerTask is not null)
            {
                try { await _displayTimerTask; }
                catch (OperationCanceledException) { }
            }

            _cts.Dispose();
            _cts = null;
        }

        // Turn off the display before closing
        if (_device.IsOpen)
        {
            try { _device.SetDisplayEnabled(false); }
            catch { }
        }

        _device.Close();
        _logger.LogInformation("Beacn Mix disconnected.");
    }

    public Task SetDialColorAsync(int dialIndex, byte r, byte g, byte b, byte a = 255)
    {
        if (!_device.IsOpen || dialIndex < 0 || dialIndex > 3)
            return Task.CompletedTask;

        var lightId = dialIndex switch
        {
            0 => BeacnConstants.LightDial1,
            1 => BeacnConstants.LightDial2,
            2 => BeacnConstants.LightDial3,
            3 => BeacnConstants.LightDial4,
            _ => throw new ArgumentOutOfRangeException(nameof(dialIndex))
        };

        try
        {
            _device.SetButtonColor(lightId, r, g, b, a);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set dial {Index} color", dialIndex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Update the group display state and signal immediate re-render.
    /// </summary>
    public void UpdateDisplay(GroupDisplayState[] groups)
    {
        _groupStates = groups;
        _displayDirtySignal.Set();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await StopAsync();
            _device.Dispose();
        }
    }

    // ── Config file watcher ────────────────────────────────────────

    private void StartConfigWatcher()
    {
        var configPath = BeacnMixConfig.GetConfigPath(_device.SerialNumber);
        var dir = Path.GetDirectoryName(configPath);
        var file = Path.GetFileName(configPath);

        if (dir is null || file is null) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _configWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _configWatcher.Changed += OnConfigFileChanged;
    }

    private void StopConfigWatcher()
    {
        if (_configWatcher is not null)
        {
            _configWatcher.Changed -= OnConfigFileChanged;
            _configWatcher.Dispose();
            _configWatcher = null;
        }
    }

    private async void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Debounce: let the file finish writing
            await Task.Delay(150);

            var newConfig = await BeacnMixConfig.LoadAsync(_device.SerialNumber);
            var oldLayout = _config.Layout;
            _config = newConfig;

            // Apply display settings that may have changed
            if (_device.IsOpen && _displayState == DisplayState.Active)
            {
                try { _device.SetDisplayBrightness(_config.DisplayBrightness); }
                catch { /* best effort */ }
            }

            // Reload layout if the layout name changed
            if (!string.Equals(oldLayout, newConfig.Layout, StringComparison.Ordinal))
            {
                _logger.LogInformation("Layout changed from '{Old}' to '{New}', reloading",
                    oldLayout, newConfig.Layout);

                _layout = await DisplayLayout.LoadAsync(newConfig.Layout);
                _displayDirtySignal.Set(); // trigger immediate re-render
            }

            _logger.LogInformation("Device config reloaded");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload device config");
        }
    }

    // ── Input polling ───────────────────────────────────────────────

    private void PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var buffer = _device.PollInput();

                if (buffer is not null)
                {
                    ProcessInput(buffer);
                }

                if (_device.UsePollMode)
                    Thread.Sleep(BeacnConstants.PollIntervalMs);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Device communication error. Device may have disconnected.");
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ProcessInput(byte[] buffer)
    {
        var hadInput = false;

        // Parse dial deltas (bytes 4-7, each is a signed byte)
        for (var d = 0; d < 4; d++)
        {
            var delta = (sbyte)buffer[4 + d];
            if (delta != 0)
            {
                hadInput = true;
                DialRotated?.Invoke(this, new DialRotatedEventArgs
                {
                    DialIndex = d,
                    Delta = delta
                });
            }
        }

        // Parse button bitmask (bytes 8-9, big-endian u16)
        var buttons = (ushort)((buffer[8] << 8) | buffer[9]);
        var changed = (ushort)(buttons ^ _lastButtons);

        if (changed != 0)
        {
            for (var bit = 0; bit < 16; bit++)
            {
                if (((changed >> bit) & 1) == 1)
                {
                    hadInput = true;
                    var pressed = ((buttons >> bit) & 1) == 1;

                    var buttonIndex = bit switch
                    {
                        BeacnConstants.ButtonDial1 => 0,
                        BeacnConstants.ButtonDial2 => 1,
                        BeacnConstants.ButtonDial3 => 2,
                        BeacnConstants.ButtonDial4 => 3,
                        _ => -1
                    };

                    if (buttonIndex >= 0)
                    {
                        ButtonPressed?.Invoke(this, new ButtonPressedEventArgs
                        {
                            ButtonIndex = buttonIndex,
                            Pressed = pressed
                        });
                    }
                }
            }

            _lastButtons = buttons;
        }

        if (hadInput)
            OnUserInput();
    }

    // ── Display state management ────────────────────────────────────

    private void OnUserInput()
    {
        _lastInputTime = DateTime.UtcNow;

        lock (_displayLock)
        {
            if (_displayState != DisplayState.Active)
            {
                _displayState = DisplayState.Active;
                try
                {
                    _device.SetDisplayEnabled(true);
                    _device.SetDisplayBrightness(_config.DisplayBrightness);
                    _displayDirtySignal.Set(); // Re-render after wake
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to wake display");
                }
            }
        }
    }

    private async Task DisplayTimerLoop(CancellationToken ct)
    {
        var lastIdleCheck = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Block until display is dirtied, or 1s elapses for idle checks
                _displayDirtySignal.Wait(1000, ct);

                if (!_device.IsOpen) continue;

                // Render immediately if signalled and screen is on
                if (_displayDirtySignal.IsSet && _displayState != DisplayState.Off)
                {
                    _displayDirtySignal.Reset();
                    try
                    {
                        var jpeg = TemplateRenderer.Render(_layout, _groupStates);
                        _device.SendImage(jpeg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update display");
                    }
                }

                // Idle checks every ~1s
                var now = DateTime.UtcNow;
                if ((now - lastIdleCheck).TotalMilliseconds < 1000) continue;
                lastIdleCheck = now;

                var idle = now - _lastInputTime;

                lock (_displayLock)
                {
                    if (_displayState == DisplayState.Active &&
                        idle.TotalSeconds >= _config.DimTimeoutSeconds)
                    {
                        _displayState = DisplayState.Dimmed;
                        try { _device.SetDisplayBrightness(_config.DimBrightness); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dim display"); }
                    }

                    if (_displayState == DisplayState.Dimmed &&
                        idle.TotalSeconds >= _config.OffTimeoutSeconds)
                    {
                        _displayState = DisplayState.Off;
                        try { _device.SetDisplayEnabled(false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to turn off display"); }
                    }
                }

                if (_displayState == DisplayState.Active && idle.TotalSeconds < 5)
                {
                    try { _device.Wake(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Keep-alive failed"); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Display timer error");
            }
        }
    }
}
