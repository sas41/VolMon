using System.IO.Pipes;
using System.Net.Sockets;
using VolMon.Core.Audio;
using VolMon.Core.Config;
using VolMon.Core.Ipc;

namespace VolMon.Daemon;

/// <summary>
/// Main hosted service for the VolMon daemon. Coordinates the audio backend,
/// stream watcher, config manager, and IPC server.
/// </summary>
public sealed class DaemonService : BackgroundService
{
    private readonly IAudioBackend _backend;
    private readonly ConfigManager _configManager;
    private readonly ILogger<DaemonService> _logger;
    private readonly ILogger<StreamWatcher> _watcherLogger;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private StreamWatcher? _watcher;
    private IpcDuplexServer? _ipcServer;

    public DaemonService(
        IAudioBackend backend,
        ConfigManager configManager,
        ILogger<DaemonService> logger,
        ILogger<StreamWatcher> watcherLogger)
    {
        _backend = backend;
        _configManager = configManager;
        _logger = logger;
        _watcherLogger = watcherLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VolMon daemon starting...");

        try
        {
            // Load config
            await _configManager.LoadAsync(stoppingToken);
            await EnsureGroupIdsAsync(stoppingToken);
            await EnsureIgnoredGroupExistsAsync(stoppingToken);
            _configManager.StartWatching();
            _configManager.StartPeriodicSave();
            _configManager.ConfigChanged += OnConfigChanged;
            _logger.LogInformation("Config loaded from {Path}", _configManager.ConfigPath);

            // Set up stream watcher
            _watcher = new StreamWatcher(_backend, _configManager, _watcherLogger);

            // Subscribe to watcher events for broadcasting to clients
            _watcher.StateChanged += OnWatcherStateChanged;

            // Start audio monitoring
            await _backend.StartMonitoringAsync(stoppingToken);
            _logger.LogInformation("Audio monitoring started");

            // Initial scan
            await _watcher.InitialScanAsync(stoppingToken);

            // Check for stale pipe / existing daemon before starting the IPC server.
            await CleanStalePipeAsync(stoppingToken);

            // Start IPC server
            _ipcServer = new IpcDuplexServer(HandleIpcRequestAsync);
            _ipcServer.Start();
            _logger.LogInformation("IPC server started on pipe '{Pipe}'", IpcConstants.PipeName);

            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VolMon daemon stopping...");
        }
        catch (Exception ex)
        {
            // Write directly to stderr — logger output may not flush before the
            // host terminates, especially under a debugger.
            Console.Error.WriteLine($"[FATAL] {ex}");
            Console.Error.Flush();
            _logger.LogCritical(ex, "VolMon daemon failed during startup");
            throw; // Re-throw so the host still triggers shutdown
        }
        finally
        {
            if (_ipcServer is not null)
                await _ipcServer.StopAsync();

            await _backend.StopMonitoringAsync();

            // Flush any pending config changes before shutting down
            _configManager.StopPeriodicSave();
            await _configManager.FlushIfDirtyAsync();
            _configManager.StopWatching();

            if (_watcher is not null)
            {
                _watcher.StateChanged -= OnWatcherStateChanged;
                _watcher.Dispose();
            }
        }
    }

    private async void OnConfigChanged(object? sender, VolMonConfig config)
    {
        _logger.LogInformation("Config reloaded from disk");
        if (_watcher is not null)
            await _watcher.ReassignAllAsync();
    }

    /// <summary>
    /// Called when the StreamWatcher's state changes (streams added/removed,
    /// devices changed, etc.). Broadcasts a state snapshot to all connected clients.
    /// </summary>
    private async void OnWatcherStateChanged(object? sender, EventArgs e)
    {
        await BroadcastStateAsync();
    }

    // ── IPC handler ──────────────────────────────────────────────────

    private async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            var response = request.Command switch
            {
                "list-groups" => HandleListGroups(),
                "list-streams" => HandleListStreams(),
                "list-devices" => HandleListDevices(),
                "set-group-volume" => await HandleSetGroupVolumeAsync(request, ct),
                "mute-group" => await HandleMuteGroupAsync(request, true, ct),
                "unmute-group" => await HandleMuteGroupAsync(request, false, ct),
                "add-group" => await HandleAddGroupAsync(request, ct),
                "remove-group" => await HandleRemoveGroupAsync(request, ct),
                "add-program" => await HandleAddProgramAsync(request, ct),
                "remove-program" => await HandleRemoveProgramAsync(request, ct),
                "add-device" => await HandleAddDeviceAsync(request, ct),
                "remove-device" => await HandleRemoveDeviceAsync(request, ct),
                "set-default-group" => await HandleSetDefaultGroupAsync(request, ct),
                "move-group" => await HandleMoveGroupAsync(request, ct),
                "set-group-color" => await HandleSetGroupColorAsync(request, ct),
                "rename-group" => await HandleRenameGroupAsync(request, ct),
                "reorder-groups" => await HandleReorderGroupsAsync(request, ct),
                "status" => HandleStatus(),
                "reload" => await HandleReloadAsync(ct),
                _ => new IpcResponse { Success = false, Error = $"Unknown command: {request.Command}" }
            };

            // Broadcast state after any mutation command
            if (response.Success && IsMutationCommand(request.Command))
                _ = BroadcastStateAsync();

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling IPC command {Command}", request.Command);
            return new IpcResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Returns true for commands that mutate state and should trigger a broadcast.
    /// </summary>
    private static bool IsMutationCommand(string command) => command switch
    {
        "set-group-volume" or "mute-group" or "unmute-group" or
        "add-group" or "remove-group" or
        "add-program" or "remove-program" or
        "add-device" or "remove-device" or
        "set-default-group" or "move-group" or
        "set-group-color" or "rename-group" or
        "reorder-groups" or "reload" => true,
        _ => false
    };

    // ── State broadcasting ───────────────────────────────────────────

    /// <summary>
    /// Builds a full state snapshot and broadcasts it to all connected clients.
    /// </summary>
    private async Task BroadcastStateAsync()
    {
        if (_ipcServer is null || _ipcServer.ClientCount == 0) return;

        try
        {
            var evt = BuildStateEvent();
            await _ipcServer.BroadcastAsync(evt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast state to clients");
        }
    }

    private IpcEvent BuildStateEvent() => new()
    {
        Name = "state-changed",
        Status = BuildStatus(),
        Groups = _configManager.Config.Groups,
        Streams = SnapshotStreams(),
        Devices = SnapshotDevices()
    };

    // ── Command handlers ─────────────────────────────────────────────

    private IpcResponse HandleListGroups() =>
        new() { Success = true, Groups = _configManager.Config.Groups };

    private IpcResponse HandleListStreams() =>
        new() { Success = true, Streams = SnapshotStreams() };

    private IpcResponse HandleListDevices() =>
        new() { Success = true, Devices = SnapshotDevices() };

    private async Task<IpcResponse> HandleSetGroupVolumeAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Volume is null)
            return new IpcResponse { Success = false, Error = "Volume is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        group.Volume = Math.Clamp(request.Volume.Value, 0, 100);
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ApplyGroupSettingsAsync(group, ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleMuteGroupAsync(IpcRequest request, bool muted, CancellationToken ct)
    {
        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        group.Muted = muted;
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ApplyGroupSettingsAsync(group, ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleAddGroupAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Group is null)
            return new IpcResponse { Success = false, Error = "Group data is required" };

        if (FindGroupByName(request.Group.Name) is not null)
            return new IpcResponse { Success = false, Error = $"Group '{request.Group.Name}' already exists" };

        // Only the daemon can create the ignored group
        request.Group.IsIgnored = false;

        // Assign a GUID if not provided
        if (request.Group.Id == Guid.Empty)
            request.Group.Id = Guid.NewGuid();

        // If this is the default group, unset any existing default
        if (request.Group.IsDefault)
        {
            foreach (var g in _configManager.Config.Groups)
                g.IsDefault = false;
        }

        _configManager.Config.Groups.Add(request.Group);
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleRemoveGroupAsync(IpcRequest request, CancellationToken ct)
    {
        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };
        if (group.IsIgnored)
            return new IpcResponse { Success = false, Error = "The Ignored group cannot be deleted" };

        _configManager.Config.Groups.Remove(group);
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleAddProgramAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.ProgramName is null)
            return new IpcResponse { Success = false, Error = "Program name is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        // Remove from any other group first
        foreach (var g in _configManager.Config.Groups)
            g.Programs.RemoveAll(p => p.Equals(request.ProgramName, StringComparison.OrdinalIgnoreCase));

        group.Programs.Add(request.ProgramName);
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleRemoveProgramAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.ProgramName is null)
            return new IpcResponse { Success = false, Error = "Program name is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        var removed = group.Programs.RemoveAll(
            p => p.Equals(request.ProgramName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return new IpcResponse { Success = false, Error = $"Program '{request.ProgramName}' not in group '{group.Name}'" };

        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleAddDeviceAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.DeviceName is null)
            return new IpcResponse { Success = false, Error = "Device name is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        // Remove from any other group first
        foreach (var g in _configManager.Config.Groups)
            g.Devices.RemoveAll(d => d.Equals(request.DeviceName, StringComparison.OrdinalIgnoreCase));

        group.Devices.Add(request.DeviceName);
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleRemoveDeviceAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.DeviceName is null)
            return new IpcResponse { Success = false, Error = "Device name is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        var removed = group.Devices.RemoveAll(
            d => d.Equals(request.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return new IpcResponse { Success = false, Error = $"Device '{request.DeviceName}' not in group '{group.Name}'" };

        _configManager.MarkDirty();

        // Reset device volume to 100% and unmute when removed from a group
        try
        {
            await _backend.SetDeviceVolumeAsync(request.DeviceName, 100, ct);
            await _backend.SetDeviceMuteAsync(request.DeviceName, false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset volume for device {Device}", request.DeviceName);
        }

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private Task<IpcResponse> HandleMoveGroupAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Direction is null)
            return Task.FromResult(new IpcResponse { Success = false, Error = "Direction is required (up or down)" });

        var group = ResolveGroup(request);
        if (group is null)
            return Task.FromResult(new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" });

        var groups = _configManager.Config.Groups;
        var index = groups.IndexOf(group);

        var newIndex = request.Direction.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? index - 1
            : index + 1;

        if (newIndex < 0 || newIndex >= groups.Count)
            return Task.FromResult(new IpcResponse { Success = true }); // Already at boundary, no-op

        // Swap
        (groups[index], groups[newIndex]) = (groups[newIndex], groups[index]);
        _configManager.MarkDirty();

        return Task.FromResult(new IpcResponse { Success = true });
    }

    private Task<IpcResponse> HandleSetGroupColorAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Color is null)
            return Task.FromResult(new IpcResponse { Success = false, Error = "Color is required" });

        var group = ResolveGroup(request);
        if (group is null)
            return Task.FromResult(new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" });

        group.Color = request.Color;
        _configManager.MarkDirty();

        return Task.FromResult(new IpcResponse { Success = true });
    }

    private Task<IpcResponse> HandleRenameGroupAsync(IpcRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NewName))
            return Task.FromResult(new IpcResponse { Success = false, Error = "New name is required" });

        var group = ResolveGroup(request);
        if (group is null)
            return Task.FromResult(new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" });
        if (group.IsIgnored)
            return Task.FromResult(new IpcResponse { Success = false, Error = "The Ignored group cannot be renamed" });

        // Check the new name isn't already taken
        if (FindGroupByName(request.NewName) is not null)
            return Task.FromResult(new IpcResponse { Success = false, Error = $"A group named '{request.NewName}' already exists" });

        group.Name = request.NewName;
        _configManager.MarkDirty();

        // No need to update AssignedGroup on streams — they reference by GUID now
        _logger.LogInformation("Renamed group {Id} to '{NewName}'", group.Id, request.NewName);
        return Task.FromResult(new IpcResponse { Success = true });
    }

    private Task<IpcResponse> HandleReorderGroupsAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.GroupOrder is null || request.GroupOrder.Count == 0)
            return Task.FromResult(new IpcResponse { Success = false, Error = "GroupOrder is required" });

        var groups = _configManager.Config.Groups;
        var ordered = new List<AudioGroup>();

        // GroupOrder contains GUID strings; fall back to name matching for CLI compat
        foreach (var entry in request.GroupOrder)
        {
            AudioGroup? group = null;
            if (Guid.TryParse(entry, out var id))
                group = FindGroupById(id);
            group ??= FindGroupByName(entry);
            if (group is not null)
                ordered.Add(group);
        }

        // Append any groups not mentioned in the order (safety net)
        foreach (var group in groups)
        {
            if (!ordered.Contains(group))
                ordered.Add(group);
        }

        groups.Clear();
        groups.AddRange(ordered);
        _configManager.MarkDirty();

        _logger.LogInformation("Groups reordered: {Order}", string.Join(", ", ordered.Select(g => g.Name)));
        return Task.FromResult(new IpcResponse { Success = true });
    }

    private async Task<IpcResponse> HandleSetDefaultGroupAsync(IpcRequest request, CancellationToken ct)
    {
        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        // Clear existing default, set new one
        foreach (var g in _configManager.Config.Groups)
            g.IsDefault = false;

        group.IsDefault = true;
        _configManager.MarkDirty();

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private IpcResponse HandleStatus() => new()
    {
        Success = true,
        Status = BuildStatus(),
        Groups = _configManager.Config.Groups,
        Streams = SnapshotStreams(),
        Devices = SnapshotDevices()
    };

    private async Task<IpcResponse> HandleReloadAsync(CancellationToken ct)
    {
        await _configManager.LoadAsync(ct);
        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    // ── Snapshot helpers ────────────────────────────────────────────

    /// <summary>Cached lowercase device-type strings to avoid ToString + ToLowerInvariant per device.</summary>
    private static readonly Dictionary<DeviceType, string> DeviceTypeStrings = new()
    {
        [DeviceType.Sink] = "sink",
        [DeviceType.Source] = "source"
    };

    private DaemonStatus BuildStatus() => new()
    {
        Running = true,
        ActiveStreams = _watcher?.ActiveStreams.Count ?? 0,
        ActiveDevices = _watcher?.KnownDevices.Count ?? 0,
        ConfiguredGroups = _configManager.Config.Groups.Count,
        StartedAt = _startedAt
    };

    private List<AudioStreamInfo> SnapshotStreams() =>
        _watcher?.ActiveStreams.Values.Select(s => new AudioStreamInfo
        {
            Id = s.Id,
            BinaryName = s.BinaryName,
            ApplicationClass = s.ApplicationClass,
            Volume = s.Volume,
            Muted = s.Muted,
            AssignedGroup = s.AssignedGroup
        }).ToList() ?? [];

    private List<AudioDeviceInfo> SnapshotDevices() =>
        _watcher?.KnownDevices.Values.Select(d => new AudioDeviceInfo
        {
            Name = d.Name,
            Description = d.Description,
            Type = DeviceTypeStrings.GetValueOrDefault(d.Type, "sink"),
            Volume = d.Volume,
            Muted = d.Muted,
            AssignedGroup = d.AssignedGroup
        }).ToList() ?? [];

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the permanent "Ignored" group exists in config.
    /// </summary>
    private async Task EnsureIgnoredGroupExistsAsync(CancellationToken ct)
    {
        if (_configManager.Config.Groups.Any(g => g.IsIgnored))
            return;

        _configManager.Config.Groups.Add(new AudioGroup
        {
            Id = Guid.NewGuid(),
            Name = "Ignored",
            IsIgnored = true,
            Volume = 0,
            Color = "#808080"
        });
        await _configManager.SaveAsync(ct);
        _logger.LogInformation("Created permanent Ignored group");
    }

    /// <summary>
    /// Assigns GUIDs to any groups that don't have one (migration from name-based config).
    /// </summary>
    private async Task EnsureGroupIdsAsync(CancellationToken ct)
    {
        var needsSave = false;
        foreach (var group in _configManager.Config.Groups)
        {
            if (group.Id == Guid.Empty)
            {
                group.Id = Guid.NewGuid();
                needsSave = true;
                _logger.LogInformation("Assigned GUID {Id} to group '{Name}'", group.Id, group.Name);
            }
        }
        if (needsSave)
            await _configManager.SaveAsync(ct);
    }

    /// <summary>
    /// Resolves a group from an IPC request. Prefers GroupId, falls back to GroupName.
    /// </summary>
    private AudioGroup? ResolveGroup(IpcRequest request) =>
        request.GroupId.HasValue
            ? FindGroupById(request.GroupId.Value)
            : request.GroupName is not null ? FindGroupByName(request.GroupName) : null;

    /// <summary>
    /// Returns a display label for the group reference in an IPC request (for error messages).
    /// </summary>
    private static string GroupLabel(IpcRequest request) =>
        request.GroupId.HasValue
            ? request.GroupId.Value.ToString()
            : request.GroupName ?? "?";

    private AudioGroup? FindGroupById(Guid id) =>
        _configManager.Config.Groups.FirstOrDefault(g => g.Id == id);

    private AudioGroup? FindGroupByName(string name) =>
        _configManager.Config.Groups.FirstOrDefault(
            g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── Stale pipe detection ────────────────────────────────────────

    /// <summary>
    /// On Linux, .NET named pipes are Unix domain sockets at /tmp/CoreFxPipe_{name}.
    /// If a previous daemon crashed or was killed without cleanup, the socket file
    /// remains and prevents a new server from binding.
    ///
    /// This method detects that situation by attempting a quick client connect:
    ///   - If it succeeds → another daemon is already running (fatal error).
    ///   - If it fails    → stale socket, delete it and proceed.
    /// </summary>
    private async Task CleanStalePipeAsync(CancellationToken ct)
    {
        // On Linux the socket path is /tmp/CoreFxPipe_{pipeName}.
        // On Windows named pipes don't leave files, so this is a no-op.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var socketPath = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{IpcConstants.PipeName}");
        if (!File.Exists(socketPath))
            return;

        _logger.LogWarning(
            "Pipe socket already exists at {Path} — checking for a running daemon...",
            socketPath);

        // Try connecting as a client with a short timeout.
        try
        {
            using var probe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut);
            await probe.ConnectAsync(TimeSpan.FromMilliseconds(500), ct);

            // Connected — another daemon is alive.
            _logger.LogCritical(
                "Another VolMon daemon is already running (pipe '{Pipe}' is active). "
                + "Kill the other instance first, or delete {Path}.",
                IpcConstants.PipeName, socketPath);

            throw new InvalidOperationException(
                $"Another VolMon daemon is already running on pipe '{IpcConstants.PipeName}'.");
        }
        catch (TimeoutException)
        {
            // Nobody answered — stale socket from a crashed process.
            _logger.LogWarning("Stale pipe socket detected — removing {Path}", socketPath);
            try { File.Delete(socketPath); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove stale pipe socket {Path}", socketPath);
                throw;
            }
        }
        catch (IOException)
        {
            // Connection refused / broken pipe — also stale.
            _logger.LogWarning("Stale pipe socket detected — removing {Path}", socketPath);
            try { File.Delete(socketPath); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove stale pipe socket {Path}", socketPath);
                throw;
            }
        }
    }
}
