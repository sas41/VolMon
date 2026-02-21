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

    // ── Save debounce ────────────────────────────────────────────────
    private CancellationTokenSource? _saveDebounceCts;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private const int SaveDebounceMs = 500;

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

        // Load config
        await _configManager.LoadAsync(stoppingToken);
        await EnsureGroupIdsAsync(stoppingToken);
        await EnsureIgnoredGroupExistsAsync(stoppingToken);
        _configManager.StartWatching();
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

        // Start IPC server
        _ipcServer = new IpcDuplexServer(HandleIpcRequestAsync);
        _ipcServer.Start();
        _logger.LogInformation("IPC server started on pipe '{Pipe}'", IpcConstants.PipeName);

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VolMon daemon stopping...");
        }
        finally
        {
            if (_ipcServer is not null)
                await _ipcServer.StopAsync();

            await _backend.StopMonitoringAsync();
            _configManager.StopWatching();
            _watcher.StateChanged -= OnWatcherStateChanged;
            _watcher.Dispose();
            _saveDebounceCts?.Dispose();
            _saveLock.Dispose();
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

    private IpcEvent BuildStateEvent()
    {
        return new IpcEvent
        {
            Name = "state-changed",
            Status = new DaemonStatus
            {
                Running = true,
                ActiveStreams = _watcher?.ActiveStreams.Count ?? 0,
                ActiveDevices = _watcher?.KnownDevices.Count ?? 0,
                ConfiguredGroups = _configManager.Config.Groups.Count,
                StartedAt = _startedAt
            },
            Groups = _configManager.Config.Groups,
            Streams = _watcher?.ActiveStreams.Values.Select(s => new AudioStreamInfo
            {
                Id = s.Id,
                BinaryName = s.BinaryName,
                ApplicationClass = s.ApplicationClass,
                Volume = s.Volume,
                Muted = s.Muted,
                AssignedGroup = s.AssignedGroup
            }).ToList() ?? [],
            Devices = _watcher?.KnownDevices.Values.Select(d => new AudioDeviceInfo
            {
                Name = d.Name,
                Description = d.Description,
                Type = d.Type.ToString().ToLowerInvariant(),
                Volume = d.Volume,
                Muted = d.Muted,
                AssignedGroup = d.AssignedGroup
            }).ToList() ?? []
        };
    }

    // ── Command handlers ─────────────────────────────────────────────

    private IpcResponse HandleListGroups() =>
        new() { Success = true, Groups = _configManager.Config.Groups };

    private IpcResponse HandleListStreams()
    {
        var streams = _watcher?.ActiveStreams.Values.Select(s => new AudioStreamInfo
        {
            Id = s.Id,
            BinaryName = s.BinaryName,
            ApplicationClass = s.ApplicationClass,
            Volume = s.Volume,
            Muted = s.Muted,
            AssignedGroup = s.AssignedGroup
        }).ToList() ?? [];

        return new IpcResponse { Success = true, Streams = streams };
    }

    private IpcResponse HandleListDevices()
    {
        var devices = _watcher?.KnownDevices.Values.Select(d => new AudioDeviceInfo
        {
            Name = d.Name,
            Description = d.Description,
            Type = d.Type.ToString().ToLowerInvariant(),
            Volume = d.Volume,
            Muted = d.Muted,
            AssignedGroup = d.AssignedGroup
        }).ToList() ?? [];

        return new IpcResponse { Success = true, Devices = devices };
    }

    private async Task<IpcResponse> HandleSetGroupVolumeAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Volume is null)
            return new IpcResponse { Success = false, Error = "Volume is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        group.Volume = Math.Clamp(request.Volume.Value, 0, 100);
        DebounceSaveAsync(ct);

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
        DebounceSaveAsync(ct);

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
        await _configManager.SaveAsync(ct);

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
        await _configManager.SaveAsync(ct);

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
        await _configManager.SaveAsync(ct);

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

        await _configManager.SaveAsync(ct);

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
        await _configManager.SaveAsync(ct);

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

        await _configManager.SaveAsync(ct);

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

    private async Task<IpcResponse> HandleMoveGroupAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Direction is null)
            return new IpcResponse { Success = false, Error = "Direction is required (up or down)" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        var groups = _configManager.Config.Groups;
        var index = groups.IndexOf(group);

        var newIndex = request.Direction.Equals("up", StringComparison.OrdinalIgnoreCase)
            ? index - 1
            : index + 1;

        if (newIndex < 0 || newIndex >= groups.Count)
            return new IpcResponse { Success = true }; // Already at boundary, no-op

        // Swap
        (groups[index], groups[newIndex]) = (groups[newIndex], groups[index]);
        await _configManager.SaveAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleSetGroupColorAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Color is null)
            return new IpcResponse { Success = false, Error = "Color is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };

        group.Color = request.Color;
        DebounceSaveAsync(ct);

        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleRenameGroupAsync(IpcRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NewName))
            return new IpcResponse { Success = false, Error = "New name is required" };

        var group = ResolveGroup(request);
        if (group is null)
            return new IpcResponse { Success = false, Error = $"Group '{GroupLabel(request)}' not found" };
        if (group.IsIgnored)
            return new IpcResponse { Success = false, Error = "The Ignored group cannot be renamed" };

        // Check the new name isn't already taken
        if (FindGroupByName(request.NewName) is not null)
            return new IpcResponse { Success = false, Error = $"A group named '{request.NewName}' already exists" };

        group.Name = request.NewName;
        await _configManager.SaveAsync(ct);

        // No need to update AssignedGroup on streams — they reference by GUID now
        _logger.LogInformation("Renamed group {Id} to '{NewName}'", group.Id, request.NewName);
        return new IpcResponse { Success = true };
    }

    private async Task<IpcResponse> HandleReorderGroupsAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.GroupOrder is null || request.GroupOrder.Count == 0)
            return new IpcResponse { Success = false, Error = "GroupOrder is required" };

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
        await _configManager.SaveAsync(ct);

        _logger.LogInformation("Groups reordered: {Order}", string.Join(", ", ordered.Select(g => g.Name)));
        return new IpcResponse { Success = true };
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
        await _configManager.SaveAsync(ct);

        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    private IpcResponse HandleStatus()
    {
        var resp = new IpcResponse
        {
            Success = true,
            Status = new DaemonStatus
            {
                Running = true,
                ActiveStreams = _watcher?.ActiveStreams.Count ?? 0,
                ActiveDevices = _watcher?.KnownDevices.Count ?? 0,
                ConfiguredGroups = _configManager.Config.Groups.Count,
                StartedAt = _startedAt
            },
            Groups = _configManager.Config.Groups,
            Streams = _watcher?.ActiveStreams.Values.Select(s => new AudioStreamInfo
            {
                Id = s.Id,
                BinaryName = s.BinaryName,
                ApplicationClass = s.ApplicationClass,
                Volume = s.Volume,
                Muted = s.Muted,
                AssignedGroup = s.AssignedGroup
            }).ToList() ?? [],
            Devices = _watcher?.KnownDevices.Values.Select(d => new AudioDeviceInfo
            {
                Name = d.Name,
                Description = d.Description,
                Type = d.Type.ToString().ToLowerInvariant(),
                Volume = d.Volume,
                Muted = d.Muted,
                AssignedGroup = d.AssignedGroup
            }).ToList() ?? []
        };

        return resp;
    }

    private async Task<IpcResponse> HandleReloadAsync(CancellationToken ct)
    {
        await _configManager.LoadAsync(ct);
        if (_watcher is not null)
            await _watcher.ReassignAllAsync(ct);

        return new IpcResponse { Success = true };
    }

    // ── Debounced config save ────────────────────────────────────────

    /// <summary>
    /// Schedules a config save after a debounce delay. Rapid calls (e.g. from
    /// volume slider drags) are coalesced into a single disk write.
    /// </summary>
    private void DebounceSaveAsync(CancellationToken ct)
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _saveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDebounceMs, token);
                await _saveLock.WaitAsync(token);
                try
                {
                    await _configManager.SaveAsync(token);
                }
                finally
                {
                    _saveLock.Release();
                }
            }
            catch (OperationCanceledException) { /* debounced away */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save config (debounced)");
            }
        }, CancellationToken.None);
    }

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
}
