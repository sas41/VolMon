using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;
using ReactiveUI;
using VolMon.Hardware;
using VolMon.Hardware.Beacn.Mix;
using VolMon.HardwareGUI.Services;

namespace VolMon.HardwareGUI.ViewModels;

public sealed class MainViewModel : ReactiveObject, IDisposable
{
    private FileSystemWatcher? _configWatcher;

    // ── Observable properties ───────────────────────────────────────

    private bool _isDaemonRunning;
    public bool IsDaemonRunning
    {
        get => _isDaemonRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDaemonRunning, value);
            DaemonButtonText = value ? "Stop Daemon" : "Start Daemon";
        }
    }

    private bool _isDaemonInstalled;
    public bool IsDaemonInstalled
    {
        get => _isDaemonInstalled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDaemonInstalled, value);
            ServiceButtonText = value ? "Uninstall Service" : "Install as Service";
        }
    }

    private string _statusText = "Loading...";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private DeviceViewModel? _selectedDevice;
    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set => this.RaiseAndSetIfChanged(ref _selectedDevice, value);
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    // ── Commands ────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDaemonCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleServiceCommand { get; }

    private string _daemonButtonText = "Start Daemon";
    public string DaemonButtonText
    {
        get => _daemonButtonText;
        set => this.RaiseAndSetIfChanged(ref _daemonButtonText, value);
    }

    private string _serviceButtonText = "Install as Service";
    public string ServiceButtonText
    {
        get => _serviceButtonText;
        set => this.RaiseAndSetIfChanged(ref _serviceButtonText, value);
    }

    // ── Available layouts (bundled presets) ──────────────────────────

    public string[] AvailableLayouts { get; }

    public MainViewModel()
    {
        AvailableLayouts = HardwareConfigService.ListBundledLayouts();

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        ToggleDaemonCommand = ReactiveCommand.CreateFromTask(ToggleDaemonAsync);
        ToggleServiceCommand = ReactiveCommand.CreateFromTask(ToggleServiceAsync);

        StartConfigWatcher();
        _ = RefreshAsync();
    }

    // ── Refresh ─────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        try
        {
            IsDaemonRunning = await CheckDaemonRunningAsync();
            IsDaemonInstalled = CheckDaemonInstalled();

            var config = await HardwareConfigService.LoadHardwareConfigAsync();

            var configIds = config.Devices.Keys.ToHashSet();

            // Remove devices no longer in config
            for (var i = Devices.Count - 1; i >= 0; i--)
            {
                if (!configIds.Contains(Devices[i].DeviceId))
                    Devices.RemoveAt(i);
            }

            // Add or update devices
            foreach (var (deviceId, entry) in config.Devices)
            {
                var existing = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (existing is not null)
                {
                    existing.Update(entry);
                }
                else
                {
                    var vm = new DeviceViewModel(deviceId, entry, AvailableLayouts, this);
                    Devices.Add(vm);
                }
            }

            StatusText = IsDaemonRunning
                ? $"Daemon running - {config.Devices.Count} device(s) configured"
                : $"Daemon stopped - {config.Devices.Count} device(s) configured";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    // ── Config persistence ──────────────────────────────────────────

    internal async Task SaveDeviceEnabledAsync(string deviceId, bool enabled)
    {
        var config = await HardwareConfigService.LoadHardwareConfigAsync();
        if (config.Devices.TryGetValue(deviceId, out var entry))
        {
            entry.Enabled = enabled;
            await HardwareConfigService.SaveHardwareConfigAsync(config);
        }
    }

    internal async Task SaveDeviceConfigAsync(string serial, BeacnMixConfig deviceConfig)
    {
        await HardwareConfigService.SaveDeviceConfigAsync(serial, deviceConfig);
    }

    // ── Config file watcher ─────────────────────────────────────────

    private void StartConfigWatcher()
    {
        var dir = HardwareConfigService.GetConfigDir();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _configWatcher = new FileSystemWatcher(dir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _configWatcher.Changed += OnConfigChanged;
        _configWatcher.Created += OnConfigChanged;
        _configWatcher.Deleted += OnConfigChanged;
    }

    private async void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(200);
            await RefreshAsync();
        }
        catch { /* best effort */ }
    }

    // ── Daemon management (cross-platform) ────────────────────────────

    private static async Task<bool> CheckDaemonRunningAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var result = await RunProcessAsync("tasklist", "/FI", "IMAGENAME eq VolMon.Hardware.exe", "/FO", "CSV", "/NH");
                return result.Stdout.Contains("VolMon.Hardware", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Linux and macOS both have pgrep
                var result = await RunProcessAsync("pgrep", "-f", "VolMon.Hardware");
                return result.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckDaemonInstalled()
    {
        if (OperatingSystem.IsLinux())
            return File.Exists(GetLinuxSystemdUnitPath());
        if (OperatingSystem.IsMacOS())
            return File.Exists(GetMacOsPlistPath());
        if (OperatingSystem.IsWindows())
        {
            var (exitCode, _) = RunSchtasks("/Query", "/TN", WindowsTaskName);
            return exitCode == 0;
        }
        return false;
    }

    private async Task ToggleDaemonAsync()
    {
        if (IsDaemonRunning)
            await StopDaemonAsync();
        else
            await StartDaemonAsync();
    }

    private async Task StartDaemonAsync()
    {
        if (IsDaemonInstalled)
        {
            // Use the service manager to start
            if (OperatingSystem.IsLinux())
                await RunProcessAsync("systemctl", "--user", "start", LinuxServiceName);
            else if (OperatingSystem.IsMacOS())
                await MacOsLoadAgent(start: true);
            else if (OperatingSystem.IsWindows())
                RunSchtasks("/Run", "/TN", WindowsTaskName);
        }
        else
        {
            // Start directly as a detached process
            var exePath = FindHardwareDaemonPath();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try { Process.Start(psi); } catch { /* best effort */ }
        }

        // Give the process a moment to start before checking
        await Task.Delay(500);
        await RefreshAsync();
    }

    private async Task StopDaemonAsync()
    {
        if (IsDaemonInstalled)
        {
            // Use the service manager to stop
            if (OperatingSystem.IsLinux())
                await RunProcessAsync("systemctl", "--user", "stop", LinuxServiceName);
            else if (OperatingSystem.IsMacOS())
                await MacOsLoadAgent(start: false);
            else if (OperatingSystem.IsWindows())
                RunSchtasks("/End", "/TN", WindowsTaskName);
        }
        else
        {
            // Kill the process directly
            if (OperatingSystem.IsWindows())
                await RunProcessAsync("taskkill", "/F", "/IM", "VolMon.Hardware.exe");
            else
                await RunProcessAsync("pkill", "-f", "VolMon.Hardware");
        }

        await Task.Delay(500);
        await RefreshAsync();
    }

    private async Task ToggleServiceAsync()
    {
        if (IsDaemonInstalled)
            await UninstallServiceAsync();
        else
            await InstallServiceAsync();
    }

    private async Task InstallServiceAsync()
    {
        var exePath = FindHardwareDaemonPath();

        if (OperatingSystem.IsLinux())
            await InstallLinuxServiceAsync(exePath);
        else if (OperatingSystem.IsMacOS())
            await InstallMacOsServiceAsync(exePath);
        else if (OperatingSystem.IsWindows())
            InstallWindowsService(exePath);

        await RefreshAsync();
    }

    private async Task UninstallServiceAsync()
    {
        if (OperatingSystem.IsLinux())
            await UninstallLinuxServiceAsync();
        else if (OperatingSystem.IsMacOS())
            await UninstallMacOsServiceAsync();
        else if (OperatingSystem.IsWindows())
            UninstallWindowsService();

        await RefreshAsync();
    }

    // ── Linux (systemd user unit) ───────────────────────────────────

    private const string LinuxServiceName = "volmon-hardware";

    private static string GetLinuxSystemdUnitPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "systemd", "user", $"{LinuxServiceName}.service");
    }

    private static async Task InstallLinuxServiceAsync(string exePath)
    {
        var unitPath = GetLinuxSystemdUnitPath();
        var unitDir = Path.GetDirectoryName(unitPath);
        if (unitDir is not null && !Directory.Exists(unitDir))
            Directory.CreateDirectory(unitDir);

        var unitContent = $"""
            [Unit]
            Description=VolMon Hardware Daemon
            After=volmon.service

            [Service]
            Type=simple
            ExecStart={exePath}
            Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target
            """;

        await File.WriteAllTextAsync(unitPath, unitContent);
        await RunProcessAsync("systemctl", "--user", "daemon-reload");
        await RunProcessAsync("systemctl", "--user", "enable", LinuxServiceName);
        await RunProcessAsync("systemctl", "--user", "start", LinuxServiceName);
    }

    private static async Task UninstallLinuxServiceAsync()
    {
        await RunProcessAsync("systemctl", "--user", "stop", LinuxServiceName);
        await RunProcessAsync("systemctl", "--user", "disable", LinuxServiceName);

        var unitPath = GetLinuxSystemdUnitPath();
        if (File.Exists(unitPath))
            File.Delete(unitPath);

        await RunProcessAsync("systemctl", "--user", "daemon-reload");
    }

    // ── macOS (launchd user agent) ──────────────────────────────────

    private const string MacOsAgentLabel = "com.volmon.hardware";

    private static string GetMacOsPlistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{MacOsAgentLabel}.plist");
    }

    private static async Task InstallMacOsServiceAsync(string exePath)
    {
        var plistPath = GetMacOsPlistPath();
        var plistDir = Path.GetDirectoryName(plistPath);
        if (plistDir is not null && !Directory.Exists(plistDir))
            Directory.CreateDirectory(plistDir);

        var plistContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacOsAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exePath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>StandardOutPath</key>
                <string>/tmp/volmon-hardware.log</string>
                <key>StandardErrorPath</key>
                <string>/tmp/volmon-hardware.err</string>
            </dict>
            </plist>
            """;

        await File.WriteAllTextAsync(plistPath, plistContent);
        await MacOsLoadAgent(start: true);
    }

    private static async Task UninstallMacOsServiceAsync()
    {
        await MacOsLoadAgent(start: false);

        var plistPath = GetMacOsPlistPath();
        if (File.Exists(plistPath))
            File.Delete(plistPath);
    }

    private static async Task MacOsLoadAgent(bool start)
    {
        var plistPath = GetMacOsPlistPath();
        if (!File.Exists(plistPath)) return;

        if (start)
        {
            // Try modern API first, fallback to legacy
            var result = await RunProcessAsync("launchctl", "load", "-w", plistPath);
            if (result.ExitCode != 0)
                await RunProcessAsync("launchctl", "bootstrap", $"gui/{GetUid()}", plistPath);
        }
        else
        {
            var result = await RunProcessAsync("launchctl", "unload", "-w", plistPath);
            if (result.ExitCode != 0)
                await RunProcessAsync("launchctl", "bootout", $"gui/{GetUid()}/{MacOsAgentLabel}");
        }
    }

    private static string GetUid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "id",
                ArgumentList = { "-u" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit(3000);
            return output ?? "501";
        }
        catch
        {
            return "501";
        }
    }

    // ── Windows (Task Scheduler) ────────────────────────────────────

    private const string WindowsTaskName = "VolMon Hardware Daemon";

    private static void InstallWindowsService(string exePath)
    {
        // Create a scheduled task that runs at logon
        RunSchtasks("/Create", "/TN", WindowsTaskName,
            "/TR", $"\"{exePath}\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");
        RunSchtasks("/Run", "/TN", WindowsTaskName);
    }

    private static void UninstallWindowsService()
    {
        RunSchtasks("/End", "/TN", WindowsTaskName);
        RunSchtasks("/Delete", "/TN", WindowsTaskName, "/F");
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(10_000);
            return (proc?.ExitCode ?? -1, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────

    private static string FindHardwareDaemonPath()
    {
        var guiDir = AppContext.BaseDirectory;

        // Platform-specific executable name
        var exeName = OperatingSystem.IsWindows() ? "VolMon.Hardware.exe" : "VolMon.Hardware";

        // Try sibling project output (dev layout)
        var candidate = Path.Combine(guiDir, "..", "VolMon.Hardware", exeName);
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);

        // Try same directory (published side-by-side)
        candidate = Path.Combine(guiDir, exeName);
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);

        // Fallback: dotnet run
        return $"dotnet run --project {Path.GetFullPath(Path.Combine(guiDir, "..", "VolMon.Hardware"))}";
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    public void Dispose()
    {
        _configWatcher?.Dispose();
    }
}

// ── Device ViewModel ────────────────────────────────────────────────

public sealed class DeviceViewModel : ReactiveObject
{
    private readonly MainViewModel _parent;

    public string DeviceId { get; }
    public string Serial { get; }
    public string[] AvailableLayouts { get; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    private string _driver = "";
    public string Driver
    {
        get => _driver;
        set => this.RaiseAndSetIfChanged(ref _driver, value);
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _enabled, value) != value) return;
            _ = _parent.SaveDeviceEnabledAsync(DeviceId, value);
        }
    }

    // ── Display support ────────────────────────────────────────────────

    private bool _hasDisplay;
    public bool HasDisplay
    {
        get => _hasDisplay;
        set => this.RaiseAndSetIfChanged(ref _hasDisplay, value);
    }

    private bool _hasDeviceConfig;
    public bool HasDeviceConfig
    {
        get => _hasDeviceConfig;
        set => this.RaiseAndSetIfChanged(ref _hasDeviceConfig, value);
    }

    private int _displayBrightness = 40;
    public int DisplayBrightness
    {
        get => _displayBrightness;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _displayBrightness, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    private int _dimBrightness = 1;
    public int DimBrightness
    {
        get => _dimBrightness;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _dimBrightness, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    private int _dimTimeoutSeconds = 30;
    public int DimTimeoutSeconds
    {
        get => _dimTimeoutSeconds;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _dimTimeoutSeconds, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    private int _offTimeoutSeconds = 60;
    public int OffTimeoutSeconds
    {
        get => _offTimeoutSeconds;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _offTimeoutSeconds, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    private int _volumeStepPerDelta = 1;
    public int VolumeStepPerDelta
    {
        get => _volumeStepPerDelta;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _volumeStepPerDelta, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    private string _layout = "VolMon_Layout_BeacnMix_default-vertical";
    public string Layout
    {
        get => _layout;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _layout, value) != value) return;
            _ = SaveDeviceConfigAsync();
        }
    }

    internal DeviceViewModel(string deviceId, DeviceEntry entry, string[] availableLayouts, MainViewModel parent)
    {
        DeviceId = deviceId;
        Serial = entry.Serial;
        AvailableLayouts = availableLayouts;
        _parent = parent;
        _name = entry.Name;
        _driver = entry.Driver;
        _enabled = entry.Enabled;
        _hasDisplay = entry.HasDisplay;

        if (entry.HasDisplay)
            _ = LoadDeviceConfigAsync();
    }

    internal void Update(DeviceEntry entry)
    {
        Name = entry.Name;
        Driver = entry.Driver;
        HasDisplay = entry.HasDisplay;
    }

    private async Task LoadDeviceConfigAsync()
    {
        try
        {
            var config = await HardwareConfigService.LoadDeviceConfigAsync(Serial);
            _displayBrightness = config.DisplayBrightness;
            _dimBrightness = config.DimBrightness;
            _dimTimeoutSeconds = config.DimTimeoutSeconds;
            _offTimeoutSeconds = config.OffTimeoutSeconds;
            _volumeStepPerDelta = config.VolumeStepPerDelta;
            _layout = config.Layout;
            HasDeviceConfig = true;

            this.RaisePropertyChanged(nameof(DisplayBrightness));
            this.RaisePropertyChanged(nameof(DimBrightness));
            this.RaisePropertyChanged(nameof(DimTimeoutSeconds));
            this.RaisePropertyChanged(nameof(OffTimeoutSeconds));
            this.RaisePropertyChanged(nameof(VolumeStepPerDelta));
            this.RaisePropertyChanged(nameof(Layout));
        }
        catch
        {
            HasDeviceConfig = false;
        }
    }

    private async Task SaveDeviceConfigAsync()
    {
        if (!HasDeviceConfig) return;

        var config = new BeacnMixConfig
        {
            DisplayBrightness = DisplayBrightness,
            DimBrightness = DimBrightness,
            DimTimeoutSeconds = DimTimeoutSeconds,
            OffTimeoutSeconds = OffTimeoutSeconds,
            VolumeStepPerDelta = VolumeStepPerDelta,
            Layout = Layout
        };

        await _parent.SaveDeviceConfigAsync(Serial, config);
    }
}
