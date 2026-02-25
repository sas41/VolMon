using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VolMon.Core.Ipc;

namespace VolMon.Hardware;

/// <summary>
/// Top-level hosted service for the hardware daemon process.
/// Connects to the VolMon daemon via IPC, then hands off to <see cref="DeviceManager"/>
/// which handles device discovery, session lifecycle, and per-device crash isolation.
/// </summary>
internal sealed class HardwareBridgeService : BackgroundService
{
    private readonly IReadOnlyList<IDeviceDriver> _drivers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HardwareBridgeService> _logger;

    private IpcDuplexClient? _ipc;
    private DeviceManager? _deviceManager;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    public HardwareBridgeService(
        IEnumerable<IDeviceDriver> drivers,
        ILoggerFactory loggerFactory,
        ILogger<HardwareBridgeService> logger)
    {
        _drivers = drivers.ToList();
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VolMon hardware daemon starting...");
        _logger.LogInformation("Registered drivers: {Drivers}",
            string.Join(", ", _drivers.Select(d => d.DriverType)));

        // Phase 1: Connect to the VolMon daemon via IPC (retry until connected)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ipc = new IpcDuplexClient();
                await _ipc.ConnectAsync(TimeSpan.FromSeconds(5), stoppingToken);
                _logger.LogInformation("Connected to VolMon daemon via IPC");
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Daemon not available yet, retrying in {Delay}s...",
                    ReconnectDelay.TotalSeconds);
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

        // Phase 2: Start the device manager
        _deviceManager = new DeviceManager(_drivers, _ipc, _loggerFactory);

        _ipc.EventReceived += OnDaemonEvent;
        _ipc.Disconnected += OnDaemonDisconnected;

        try
        {
            await _deviceManager.StartAsync(stoppingToken);
            _logger.LogInformation("Device manager started");

            // Phase 3: Idle until stopped
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_ipc is not null)
            {
                _ipc.EventReceived -= OnDaemonEvent;
                _ipc.Disconnected -= OnDaemonDisconnected;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VolMon hardware daemon stopping...");

        if (_deviceManager is not null)
            await _deviceManager.DisposeAsync();

        if (_ipc is not null)
            await _ipc.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }

    private void OnDaemonEvent(object? sender, IpcEvent e)
    {
        if (e.Name == "state-changed" && e.Groups is not null)
            _deviceManager?.BroadcastStateChanged(e.Groups, e.Processes, e.Devices);
    }

    private void OnDaemonDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("Lost connection to VolMon daemon");
    }
}
