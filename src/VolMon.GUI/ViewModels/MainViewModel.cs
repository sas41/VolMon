using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using VolMon.Core.Audio;
using VolMon.Core.Config;
using VolMon.Core.Ipc;
using VolMon.GUI.Services;

namespace VolMon.GUI.ViewModels;

// ═════════════════════════════════════════════════════════════════════
// ObservableCollection diff-merge helper
// ═════════════════════════════════════════════════════════════════════

/// <summary>
/// Reconciles an <see cref="ObservableCollection{T}"/> with a desired list of
/// items using a key-based diff: removes stale items, updates matching items
/// in place, and inserts new items — all without clearing the collection.
///
/// This avoids the UI flicker caused by Clear() + re-Add() and preserves
/// object identity for items that still exist (e.g. keeping slider drag
/// state, debounce timers, scroll position).
/// </summary>
internal static class CollectionReconciler
{
    /// <summary>
    /// Reconciles <paramref name="collection"/> to match <paramref name="desired"/>.
    /// Items are matched by key. Matched items are updated via <paramref name="update"/>.
    /// Order is preserved to match <paramref name="desired"/>.
    /// </summary>
    public static void Reconcile<TItem, TKey>(
        ObservableCollection<TItem> collection,
        IReadOnlyList<TItem> desired,
        Func<TItem, TKey> keyOf,
        Action<TItem, TItem>? update = null) where TKey : notnull
    {
        // Build set of desired keys for removal pass
        var desiredKeys = new HashSet<TKey>();
        foreach (var item in desired)
            desiredKeys.Add(keyOf(item));

        // Pass 1: Remove items no longer in the desired set
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(keyOf(collection[i])))
                collection.RemoveAt(i);
        }

        // Pass 2: Upsert and reorder to match desired
        for (int i = 0; i < desired.Count; i++)
        {
            var desiredKey = keyOf(desired[i]);

            if (i < collection.Count && EqualityComparer<TKey>.Default.Equals(keyOf(collection[i]), desiredKey))
            {
                // Already in the right position — update in place
                update?.Invoke(collection[i], desired[i]);
            }
            else
            {
                // Find this key elsewhere in the collection
                var existingIndex = -1;
                for (int j = i + 1; j < collection.Count; j++)
                {
                    if (EqualityComparer<TKey>.Default.Equals(keyOf(collection[j]), desiredKey))
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    // Move existing item to the correct position
                    collection.Move(existingIndex, i);
                    update?.Invoke(collection[i], desired[i]);
                }
                else
                {
                    // New item — insert at the correct position
                    collection.Insert(i, desired[i]);
                }
            }
        }

        // Pass 3: Trim any trailing excess (shouldn't happen, but safety)
        while (collection.Count > desired.Count)
            collection.RemoveAt(collection.Count - 1);
    }
}

public class MainViewModel : ReactiveObject
{
    public static readonly string[] ColorPalette =
    [
        "#FF9500", "#00B4D8", "#E63946", "#2EC4B6",
        "#9B59B6", "#F1C40F", "#1ABC9C", "#E74C3C",
        "#3498DB", "#E67E22"
    ];

    private IpcDuplexClient? _client;
    private bool _isConnecting;
    private string? _lastStateFingerprint;

    // ── Shortcut target state ────────────────────────────────────────
    private Guid? _targetGroupId;

    private string _daemonStatusText = "Connecting...";
    private IBrush _daemonStatusColor = Brushes.Gray;
    private string _statusSummary = "";
    private string _newGroupName = "";
    private bool _showSettings;

    public string DaemonStatusText
    {
        get => _daemonStatusText;
        set => this.RaiseAndSetIfChanged(ref _daemonStatusText, value);
    }

    public IBrush DaemonStatusColor
    {
        get => _daemonStatusColor;
        set => this.RaiseAndSetIfChanged(ref _daemonStatusColor, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        set => this.RaiseAndSetIfChanged(ref _statusSummary, value);
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set => this.RaiseAndSetIfChanged(ref _newGroupName, value);
    }

    public bool ShowSettings
    {
        get => _showSettings;
        set => this.RaiseAndSetIfChanged(ref _showSettings, value);
    }

    public ObservableCollection<ShortcutBindingViewModel> ShortcutBindings { get; } = [];

    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }

    public ObservableCollection<GroupColumnViewModel> Groups { get; } = [];
    public ObservableCollection<PoolItemViewModel> UnassignedInputs { get; } = [];
    public ObservableCollection<PoolItemViewModel> UnassignedOutputs { get; } = [];
    public ObservableCollection<PoolItemViewModel> UnassignedPrograms { get; } = [];

    public ReactiveCommand<Unit, Unit> AddGroupCommand { get; }

    public MainViewModel()
    {
        var canAddGroup = this.WhenAnyValue(x => x.NewGroupName)
            .Select(name => !string.IsNullOrWhiteSpace(name));
        AddGroupCommand = ReactiveCommand.CreateFromTask(AddGroupAsync, canAddGroup);

        ToggleSettingsCommand = ReactiveCommand.Create(() =>
        {
            ShowSettings = !ShowSettings;
        });

        _ = LoadShortcutBindingsAsync();
        _ = ConnectAsync();
    }

    // ── Connection management ────────────────────────────────────────

    /// <summary>
    /// Connects to the daemon with automatic reconnection on failure.
    /// Called on startup and when the connection drops.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_isConnecting) return;
        _isConnecting = true;

        try
        {
            // Dispose any existing client
            if (_client is not null)
            {
                _client.EventReceived -= OnEventReceived;
                _client.Disconnected -= OnDisconnected;
                await _client.DisposeAsync();
                _client = null;
            }

            _client = new IpcDuplexClient();
            _client.EventReceived += OnEventReceived;
            _client.Disconnected += OnDisconnected;

            await _client.ConnectAsync();

            // Request initial state
            var resp = await _client.SendAsync(new IpcRequest { Command = "status" });
            if (resp.Success && resp.Status is not null)
            {
                Dispatcher.UIThread.Post(() => RefreshData(resp));
            }
        }
        catch
        {
            DaemonStatusText = "Not running";
            DaemonStatusColor = Brushes.Red;
            StatusSummary = "Cannot connect to daemon";

            // Retry after a delay
            _ = ReconnectAfterDelayAsync();
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private async Task ReconnectAfterDelayAsync()
    {
        await Task.Delay(3000);
        await ConnectAsync();
    }

    /// <summary>
    /// Called when the daemon pushes a state-changed event.
    /// </summary>
    private void OnEventReceived(object? sender, IpcEvent evt)
    {
        if (evt.Name != "state-changed") return;

        // Marshal to UI thread — pass event fields directly to avoid an
        // unnecessary IpcResponse wrapper allocation.
        Dispatcher.UIThread.Post(() =>
            RefreshData(evt.Status, evt.Groups, evt.Processes, evt.Devices, evt.IgnoredPrograms));
    }

    /// <summary>
    /// Called when the persistent connection drops.
    /// </summary>
    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DaemonStatusText = "Disconnected";
            DaemonStatusColor = Brushes.Red;
            StatusSummary = "Connection to daemon lost";
        });

        // Auto-reconnect
        _ = ReconnectAfterDelayAsync();
    }

    // ── Data refresh (called from push events) ───────────────────────

    private void RefreshData(IpcResponse resp) =>
        RefreshData(resp.Status, resp.Groups, resp.Processes, resp.Devices, resp.IgnoredPrograms);

    private void RefreshData(
        DaemonStatus? status,
        List<AudioGroup>? groups,
        List<AudioProcessInfo>? processes,
        List<AudioDeviceInfo>? devices,
        List<string>? ignoredPrograms = null)
    {
        // Quick fingerprint to skip expensive reconciliation when nothing changed.
        // We combine the cheapest-to-read counts + mutable scalars into a string
        // that changes whenever the UI would need to update.
        var fingerprint = BuildFingerprint(status, groups, processes, devices);
        if (fingerprint == _lastStateFingerprint)
            return;
        _lastStateFingerprint = fingerprint;

        if (status is not null)
        {
            DaemonStatusText = "Running";
            DaemonStatusColor = Brushes.Green;
            StatusSummary = $"Streams: {status.ActiveStreams}  |  Devices: {status.ActiveDevices}  |  Groups: {status.ConfiguredGroups}";
        }

        // ── Groups
        var desiredGroups = new List<GroupColumnViewModel>();
        if (groups is not null)
        {
            var ci = 0;
            foreach (var g in groups)
            {
                var color = g.Color ?? ColorPalette[ci % ColorPalette.Length];
                desiredGroups.Add(new GroupColumnViewModel(g, color, SendCommandAsync, SendQuietAsync));
                ci++;
            }
        }

        CollectionReconciler.Reconcile(Groups, desiredGroups,
            keyOf: g => g.Id,
            update: (existing, fresh) => existing.UpdateFrom(fresh));

        // ── Processes → group members or Applications pool
        // Each AudioProcessInfo is already deduplicated by binary name by the daemon.
        // A process with streams uses the best stream assignment (real group wins
        // over Ignored/unassigned). A process with no streams but included by the
        // daemon is running-but-silent and shown as running in its configured group.

        var desiredMembers = new Dictionary<Guid, List<GroupMemberViewModel>>();
        foreach (var gvm in Groups)
            desiredMembers[gvm.Id] = new List<GroupMemberViewModel>();

        var desiredPrograms = new List<PoolItemViewModel>();

        var ignoredSet = new HashSet<string>(
            ignoredPrograms ?? [], StringComparer.OrdinalIgnoreCase);

        if (processes is not null)
        {
            foreach (var proc in processes)
            {
                // Ignored programs go to the Applications pool with IsIgnored=true.
                if (ignoredSet.Contains(proc.Name))
                {
                    desiredPrograms.Add(new PoolItemViewModel(proc.Name, "program", proc.Name, isIgnored: true));
                    continue;
                }

                // Determine the best group assignment from this process's streams.
                Guid? bestGroupId = null;
                foreach (var s in proc.Streams)
                {
                    if (s.AssignedGroup is not null && bestGroupId is null)
                        bestGroupId = s.AssignedGroup;
                }

                // A process with no streams is running-but-silent; the daemon only
                // sends it if it is configured, so find which group has it.
                if (bestGroupId is null && proc.Streams.Count == 0)
                {
                    foreach (var gvm in Groups)
                    {
                        if (gvm.ConfiguredPrograms.Contains(proc.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            bestGroupId = gvm.Id;
                            break;
                        }
                    }
                }

                if (bestGroupId is { } assignedId && desiredMembers.ContainsKey(assignedId))
                {
                    desiredMembers[assignedId].Add(new GroupMemberViewModel(
                        proc.Name, "program", proc.Name, assignedId, isRunning: true));
                }
                else if (proc.Streams.Count > 0)
                {
                    // Has streams but no group — goes to Applications pool.
                    desiredPrograms.Add(new PoolItemViewModel(proc.Name, "program", proc.Name));
                }
                // Processes with no streams and no group are not shown (per spec).
            }
        }

        // ── Disconnected programs: configured in a group but not currently running
        foreach (var gvm in Groups)
        {
            var running = desiredMembers[gvm.Id]
                .Where(m => m.ItemType == "program")
                .Select(m => m.Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var prog in gvm.ConfiguredPrograms)
            {
                if (!running.Contains(prog))
                {
                    desiredMembers[gvm.Id].Add(new GroupMemberViewModel(
                        prog, "program", prog, gvm.Id, isRunning: false));
                }
            }
        }

        // ── Devices → group members or device pools
        var desiredInputs = new List<PoolItemViewModel>();
        var desiredOutputs = new List<PoolItemViewModel>();

        if (devices is not null)
        {
            foreach (var d in devices)
            {
                var display = d.Description ?? d.Name;
                var isInput = d.Type.Equals("source", StringComparison.OrdinalIgnoreCase);
                var itemType = isInput ? "input-device" : "output-device";

                if (d.AssignedGroup is { } devGroupId && desiredMembers.ContainsKey(devGroupId))
                {
                    desiredMembers[devGroupId].Add(new GroupMemberViewModel(
                        display, itemType, d.Name, devGroupId, isRunning: true));
                }
                else
                {
                    (isInput ? desiredInputs : desiredOutputs)
                        .Add(new PoolItemViewModel(display, itemType, d.Name));
                }
            }
        }

        // ── Reconcile all member collections
        foreach (var gvm in Groups)
        {
            if (desiredMembers.TryGetValue(gvm.Id, out var members))
            {
                CollectionReconciler.Reconcile(gvm.Members, members,
                    keyOf: m => (m.ItemType, m.Identifier),
                    update: (existing, fresh) =>
                    {
                        // GroupMemberViewModel is immutable — replace if state differs
                        if (existing.IsRunning != fresh.IsRunning ||
                            existing.DisplayName != fresh.DisplayName)
                        {
                            var idx = gvm.Members.IndexOf(existing);
                            if (idx >= 0) gvm.Members[idx] = fresh;
                        }
                    });
            }
        }

        // ── Reconcile pool collections
        CollectionReconciler.Reconcile(UnassignedPrograms, desiredPrograms,
            keyOf: p => (p.ItemType, p.Identifier));

        CollectionReconciler.Reconcile(UnassignedInputs, desiredInputs,
            keyOf: p => (p.ItemType, p.Identifier));

        CollectionReconciler.Reconcile(UnassignedOutputs, desiredOutputs,
            keyOf: p => (p.ItemType, p.Identifier));
    }

    /// <summary>
    /// Builds a cheap fingerprint of the state snapshot so we can skip
    /// the full reconciliation when nothing has changed.
    /// </summary>
    private static string BuildFingerprint(
        DaemonStatus? status,
        List<AudioGroup>? groups,
        List<AudioProcessInfo>? processes,
        List<AudioDeviceInfo>? devices)
    {
        // Use a simple DefaultInterpolatedStringHandler to avoid StringBuilder alloc.
        // The fingerprint includes everything the UI renders.
        var sb = new System.Text.StringBuilder(256);

        if (status is not null)
            sb.Append(status.ActiveStreams).Append(',')
              .Append(status.ActiveDevices).Append(',')
              .Append(status.ConfiguredGroups).Append(';');

        if (groups is not null)
        {
            foreach (var g in groups)
            {
                sb.Append(g.Id).Append(':')
                  .Append(g.Name).Append(':')
                  .Append(g.Volume).Append(':')
                  .Append(g.Muted).Append(':')
                  .Append(g.IsDefault).Append(':')
                  .Append(g.Color).Append(':')
                  .Append(g.Programs.Count).Append(':')
                  .Append(g.Devices.Count).Append('|');
            }
        }
        sb.Append(';');

        if (processes is not null)
        {
            foreach (var p in processes)
            {
                sb.Append(p.Name).Append(':').Append(p.Streams.Count).Append('|');
                foreach (var s in p.Streams)
                {
                    sb.Append(s.Id).Append(':')
                      .Append(s.AssignedGroup).Append(':')
                      .Append(s.Volume).Append(':')
                      .Append(s.Muted).Append('|');
                }
            }
        }
        sb.Append(';');

        if (devices is not null)
        {
            foreach (var d in devices)
            {
                sb.Append(d.Name).Append(':')
                  .Append(d.AssignedGroup).Append(':')
                  .Append(d.Volume).Append(':')
                  .Append(d.Muted).Append('|');
            }
        }

        return sb.ToString();
    }

    // ── Group actions ────────────────────────────────────────────────

    private async Task AddGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (string.IsNullOrEmpty(name) || _client is null) return;

        await _client.SendAsync(new IpcRequest
        {
            Command = "add-group",
            Group = new AudioGroup { Name = name, Volume = 100 }
        });

        NewGroupName = "";
        // No need to RefreshAsync — the daemon will push a state-changed event
    }

    public async Task RenameGroupAsync(Guid groupId, string newName)
    {
        if (_client is null) return;

        await _client.SendAsync(new IpcRequest
        {
            Command = "rename-group",
            GroupId = groupId,
            NewName = newName
        });
        // No need to RefreshAsync — the daemon will push a state-changed event
    }

    // ── Shortcut settings ───────────────────────────────────────────

    private async Task LoadShortcutBindingsAsync()
    {
        using var configManager = new ConfigManager();
        try { await configManager.LoadAsync(); }
        catch { /* defaults */ }

        // Restore last shortcut target from previous session
        if (configManager.Config.LastTargetGroupId is { } savedId)
            _targetGroupId = savedId;

        var sc = configManager.Config.Shortcuts;
        var desired = new List<ShortcutBindingViewModel>
        {
            new("Volume Up", nameof(sc.VolumeUp), sc.VolumeUp),
            new("Volume Down", nameof(sc.VolumeDown), sc.VolumeDown),
            new("Next Group", nameof(sc.SelectNextGroup), sc.SelectNextGroup),
            new("Previous Group", nameof(sc.SelectPreviousGroup), sc.SelectPreviousGroup),
            new("Mute Toggle", nameof(sc.MuteToggle), sc.MuteToggle)
        };

        CollectionReconciler.Reconcile(ShortcutBindings, desired,
            keyOf: s => s.ConfigProperty,
            update: (existing, fresh) =>
            {
                if (existing.KeyCode != fresh.KeyCode)
                    existing.KeyCode = fresh.KeyCode;
            });
    }

    /// <summary>
    /// Saves current shortcut bindings to config and reconfigures the hotkey service.
    /// </summary>
    public async Task SaveShortcutsAsync()
    {
        using var configManager = new ConfigManager();
        try { await configManager.LoadAsync(); }
        catch { /* defaults */ }

        var sc = configManager.Config.Shortcuts;
        foreach (var binding in ShortcutBindings)
        {
            switch (binding.ConfigProperty)
            {
                case nameof(sc.VolumeUp): sc.VolumeUp = binding.KeyCode; break;
                case nameof(sc.VolumeDown): sc.VolumeDown = binding.KeyCode; break;
                case nameof(sc.SelectNextGroup): sc.SelectNextGroup = binding.KeyCode; break;
                case nameof(sc.SelectPreviousGroup): sc.SelectPreviousGroup = binding.KeyCode; break;
                case nameof(sc.MuteToggle): sc.MuteToggle = binding.KeyCode; break;
            }
        }

        try { await configManager.SaveAsync(); }
        catch { /* best-effort */ }

        // Notify App to reconfigure the hotkey service
        ShortcutsChanged?.Invoke(sc);
    }

    /// <summary>
    /// Raised when shortcuts are saved, so App.axaml.cs can reconfigure the hotkey service.
    /// </summary>
    public event Action<ShortcutConfig>? ShortcutsChanged;

    /// <summary>
    /// Removes the key binding for a shortcut (sets it to empty).
    /// </summary>
    public async Task ClearShortcutAsync(ShortcutBindingViewModel binding)
    {
        binding.KeyCode = "";
        await SaveShortcutsAsync();
    }

    // ── Drag-drop ────────────────────────────────────────────────────

    public async Task AssignToGroupAsync(Guid groupId, string itemType, string id)
    {
        if (_client is null) return;

        var cmd = itemType == "program" ? "add-program" : "add-device";
        var req = itemType == "program"
            ? new IpcRequest { Command = cmd, GroupId = groupId, ProgramName = id }
            : new IpcRequest { Command = cmd, GroupId = groupId, DeviceName = id };

        await _client.SendAsync(req);
        // No need to RefreshAsync — the daemon will push a state-changed event
    }

    public async Task ReturnToPoolAsync(Guid currentGroupId, string itemType, string id)
    {
        if (_client is null) return;

        if (itemType == "program")
        {
            // Dragging a program out of a group sends it to the ignored list.
            await _client.SendAsync(new IpcRequest
            {
                Command = "ignore-program",
                ProgramName = id
            });
        }
        else
        {
            await _client.SendAsync(new IpcRequest
            {
                Command = "remove-device",
                GroupId = currentGroupId,
                DeviceName = id
            });
        }
        // No need to RefreshAsync — the daemon will push a state-changed event
    }

    public async Task ReorderGroupAsync(Guid sourceId, Guid targetId)
    {
        if (_client is null) return;

        var src = Groups.FirstOrDefault(g => g.Id == sourceId);
        var tgt = Groups.FirstOrDefault(g => g.Id == targetId);
        if (src is null || tgt is null || src == tgt) return;

        Groups.Move(Groups.IndexOf(src), Groups.IndexOf(tgt));

        var order = Groups.Select(g => g.Id.ToString()).ToList();

        try
        {
            await _client.SendAsync(new IpcRequest
            {
                Command = "reorder-groups",
                GroupOrder = order
            });
        }
        catch { /* best-effort */ }
    }

    // ── Hotkey actions ──────────────────────────────────────────────

    /// <summary>
    /// Raised when a hotkey action changes the targeted group or its volume.
    /// Carries (groupName, volume, muted, colorHex) for the overlay.
    /// </summary>
    public event Action<string, int, bool, string>? OverlayRequested;

    /// <summary>
    /// Handles a global hotkey action. Called from the UI thread.
    /// </summary>
    public async Task HandleHotkeyAsync(HotkeyAction action)
    {
        if (Groups.Count == 0) return;

        if (action == HotkeyAction.SelectNextGroup)
        {
            SelectNextGroup();
            var g = GetTargetGroup();
            if (g is not null)
                OverlayRequested?.Invoke(g.Name, g.Volume, g.Muted, g.ColorHex);
            return;
        }

        if (action == HotkeyAction.SelectPreviousGroup)
        {
            SelectPreviousGroup();
            var g = GetTargetGroup();
            if (g is not null)
                OverlayRequested?.Invoke(g.Name, g.Volume, g.Muted, g.ColorHex);
            return;
        }

        // Ensure we have a target
        EnsureTarget();
        var target = GetTargetGroup();
        if (target is null) return;

        if (action == HotkeyAction.MuteToggle)
        {
            // Toggle mute (the setter fires the IPC command)
            target.Muted = !target.Muted;
            OverlayRequested?.Invoke(target.Name, target.Volume, target.Muted, target.ColorHex);
            return;
        }

        int delta = action == HotkeyAction.VolumeUp ? 5 : -5;
        int newVolume = Math.Clamp(target.Volume + delta, 0, 100);

        // Update locally (triggers the debounced IPC send via the setter)
        target.Volume = newVolume;

        OverlayRequested?.Invoke(target.Name, newVolume, target.Muted, target.ColorHex);
    }

    private void SelectNextGroup()
    {
        // Only consider non-skipped groups
        var eligible = Groups.Where(g => !g.SkipShortcut).ToList();
        if (eligible.Count == 0) return;

        var currentIndex = eligible.FindIndex(g => g.Id == _targetGroupId);
        var nextIndex = (currentIndex + 1) % eligible.Count;
        SetTarget(eligible[nextIndex].Id);
    }

    private void SelectPreviousGroup()
    {
        var eligible = Groups.Where(g => !g.SkipShortcut).ToList();
        if (eligible.Count == 0) return;

        var currentIndex = eligible.FindIndex(g => g.Id == _targetGroupId);
        var prevIndex = currentIndex <= 0 ? eligible.Count - 1 : currentIndex - 1;
        SetTarget(eligible[prevIndex].Id);
    }

    private void EnsureTarget()
    {
        // If current target is valid and not skip-flagged, keep it.
        if (_targetGroupId.HasValue)
        {
            var current = Groups.FirstOrDefault(g => g.Id == _targetGroupId.Value);
            if (current is not null && !current.SkipShortcut)
                return;
        }

        // Fall back to first non-skipped group.
        var first = Groups.FirstOrDefault(g => !g.SkipShortcut);
        if (first is not null)
            SetTarget(first.Id);
    }

    /// <summary>Sets the shortcut target group and persists it to config.</summary>
    private void SetTarget(Guid id)
    {
        if (_targetGroupId == id) return;
        _targetGroupId = id;
        _ = PersistTargetGroupAsync(id);
    }

    /// <summary>Saves LastTargetGroupId to the config file (best-effort, fire-and-forget).</summary>
    private static async Task PersistTargetGroupAsync(Guid id)
    {
        try
        {
            using var cm = new ConfigManager();
            await cm.LoadAsync();
            cm.Config.LastTargetGroupId = id;
            await cm.SaveAsync();
        }
        catch { /* best-effort */ }
    }

    private GroupColumnViewModel? GetTargetGroup()
    {
        if (!_targetGroupId.HasValue) return null;
        return Groups.FirstOrDefault(g => g.Id == _targetGroupId.Value);
    }

    private int GetTargetIndex()
    {
        if (!_targetGroupId.HasValue) return -1;
        for (int i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id == _targetGroupId.Value)
                return i;
        }
        return -1;
    }

    // ── IPC helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a command and waits for the response. Used for structural changes
    /// like delete and set-default where we need the response.
    /// The daemon will push a state-changed event afterward, so no manual
    /// refresh is needed.
    /// </summary>
    private async Task SendCommandAsync(IpcRequest request)
    {
        if (_client is null) return;
        await _client.SendAsync(request);
    }

    /// <summary>
    /// Fire-and-forget send for high-frequency operations (volume, mute, color).
    /// The daemon will push a state-changed event.
    /// </summary>
    private async Task SendQuietAsync(IpcRequest request)
    {
        if (_client is null) return;
        try { await _client.SendFireAndForgetAsync(request); }
        catch { /* fire-and-forget */ }
    }
}

// ═════════════════════════════════════════════════════════════════════
// GroupColumnViewModel
// ═════════════════════════════════════════════════════════════════════

public class GroupColumnViewModel : ReactiveObject
{
    private readonly Func<IpcRequest, Task> _sendCommand;
    private readonly Func<IpcRequest, Task> _sendQuiet;
    private int _volume;
    private bool _muted;
    private bool _isDefault;
    private bool _skipShortcut;
    private string _colorHex;
    private IBrush _colorBrush;
    private CancellationTokenSource? _volumeDebounce;
    private bool _muteSending;

    public Guid Id { get; }
    public string Name { get; }
    public string VolumeText => $"{_volume}%";

    /// <summary>Programs configured in this group (for disconnected indicators).</summary>
    public IReadOnlyList<string> ConfiguredPrograms { get; private set; }

    public string ColorHex
    {
        get => _colorHex;
        private set => this.RaiseAndSetIfChanged(ref _colorHex, value);
    }

    public IBrush ColorBrush
    {
        get => _colorBrush;
        private set => this.RaiseAndSetIfChanged(ref _colorBrush, value);
    }

    public ObservableCollection<GroupMemberViewModel> Members { get; } = [];

    public int Volume
    {
        get => _volume;
        set
        {
            if (_volume == value) return;
            this.RaiseAndSetIfChanged(ref _volume, value);
            this.RaisePropertyChanged(nameof(VolumeText));

            var old = _volumeDebounce;
            old?.Cancel();

            var cts = new CancellationTokenSource();
            _volumeDebounce = cts;
            var ct = cts.Token;

            old?.Dispose();

            var v = value;
            _ = DebounceVolumeAsync(v, ct);
        }
    }

    private async Task DebounceVolumeAsync(int volume, CancellationToken ct)
    {
        try
        {
            await Task.Delay(80, ct);
            await _sendQuiet(new IpcRequest
            {
                Command = "set-group-volume",
                GroupId = Id,
                Volume = volume
            });
            // Clear the debounce token so UpdateFrom() knows the daemon now
            // has the latest value and can resume syncing volume from it.
            if (_volumeDebounce?.Token == ct)
                _volumeDebounce = null;
        }
        catch (TaskCanceledException) { }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            this.RaiseAndSetIfChanged(ref _muted, value);
            _ = SendMuteAsync(value);
        }
    }

    private async Task SendMuteAsync(bool muted)
    {
        _muteSending = true;
        try
        {
            await _sendQuiet(new IpcRequest
            {
                Command = muted ? "mute-group" : "unmute-group",
                GroupId = Id
            });
        }
        finally
        {
            _muteSending = false;
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        private set => this.RaiseAndSetIfChanged(ref _isDefault, value);
    }

    /// <summary>
    /// Toggles the default state. If already default, unsets it (no default group).
    /// If not default, sets this group as default (daemon clears others).
    /// </summary>
    public void ToggleDefault()
    {
        if (_isDefault)
        {
            IsDefault = false;
            _ = _sendCommand(new IpcRequest { Command = "unset-default-group", GroupId = Id });
        }
        else
        {
            IsDefault = true;
            _ = _sendCommand(new IpcRequest { Command = "set-default-group", GroupId = Id });
        }
    }

    public bool SkipShortcut
    {
        get => _skipShortcut;
        private set => this.RaiseAndSetIfChanged(ref _skipShortcut, value);
    }

    /// <summary>
    /// Toggles whether this group is skipped by the shortcut cycle.
    /// The daemon persists the change; the GUI optimistically flips locally.
    /// </summary>
    public void ToggleSkipShortcut()
    {
        SkipShortcut = !_skipShortcut;
        _ = _sendCommand(new IpcRequest { Command = "toggle-skip-shortcut", GroupId = Id });
    }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<string, Unit> ChangeColorCommand { get; }

    public static string[] AvailableColors { get; } =
    [
        "#FF9500", "#00B4D8", "#E63946", "#2EC4B6",
        "#9B59B6", "#F1C40F", "#1ABC9C", "#E74C3C",
        "#3498DB", "#E67E22", "#FF6B6B", "#4ECDC4",
        "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD",
        "#98D8C8", "#F7DC6F", "#BB8FCE", "#85C1E9"
    ];

    public GroupColumnViewModel(AudioGroup group, string colorHex,
        Func<IpcRequest, Task> sendCommand, Func<IpcRequest, Task> sendQuiet)
    {
        _sendCommand = sendCommand;
        _sendQuiet = sendQuiet;
        Id = group.Id;
        Name = group.Name;
        ConfiguredPrograms = group.Programs;
        _volume = group.Volume;
        _muted = group.Muted;
        _isDefault = group.IsDefault;
        _skipShortcut = group.SkipShortcut;
        _colorHex = colorHex;

        try { _colorBrush = SolidColorBrush.Parse(colorHex); }
        catch { _colorBrush = Brushes.Gray; }

        DeleteCommand = ReactiveCommand.CreateFromTask(
            () => _sendCommand(new IpcRequest { Command = "remove-group", GroupId = Id }));

        ChangeColorCommand = ReactiveCommand.CreateFromTask<string>(async color =>
        {
            ColorHex = color;
            try { ColorBrush = SolidColorBrush.Parse(color); }
            catch { ColorBrush = Brushes.Gray; }

            await _sendQuiet(new IpcRequest
            {
                Command = "set-group-color",
                GroupId = Id,
                Color = color
            });
        });
    }

    /// <summary>
    /// Updates mutable properties from a freshly created instance. Used by
    /// the collection reconciler to patch in place without destroying the VM
    /// (preserving debounce timers, slider drag state, etc.).
    ///
    /// Only updates properties that differ from the current values. Volume
    /// and Muted are set via backing fields to avoid triggering IPC commands
    /// (the daemon already has the authoritative values).
    ///
    /// Volume is skipped when a debounce is pending (the user's local value
    /// is authoritative until the debounced IPC send completes). Muted is
    /// skipped while a mute command is in-flight.
    /// </summary>
    public void UpdateFrom(GroupColumnViewModel source)
    {
        ConfiguredPrograms = source.ConfiguredPrograms;

        // Skip volume sync while a debounce is pending — the user's slider
        // position is authoritative until the debounced send completes and
        // the daemon acknowledges the new value.
        var debounceActive = _volumeDebounce is not null && !_volumeDebounce.IsCancellationRequested;
        if (!debounceActive && _volume != source._volume)
        {
            _volume = source._volume;
            this.RaisePropertyChanged(nameof(Volume));
            this.RaisePropertyChanged(nameof(VolumeText));
        }

        // Skip muted sync while a mute/unmute command is in-flight.
        if (!_muteSending && _muted != source._muted)
        {
            _muted = source._muted;
            this.RaisePropertyChanged(nameof(Muted));
        }

        if (_isDefault != source._isDefault)
        {
            _isDefault = source._isDefault;
            this.RaisePropertyChanged(nameof(IsDefault));
        }

        if (_skipShortcut != source._skipShortcut)
        {
            _skipShortcut = source._skipShortcut;
            this.RaisePropertyChanged(nameof(SkipShortcut));
        }

        if (_colorHex != source._colorHex)
        {
            ColorHex = source._colorHex;
            ColorBrush = source._colorBrush;
        }
    }
}

// ═════════════════════════════════════════════════════════════════════
// GroupMemberViewModel
// ═════════════════════════════════════════════════════════════════════

public class GroupMemberViewModel
{
    private static readonly IBrush RunningBrush = SolidColorBrush.Parse("#DDDDDD");
    private static readonly IBrush DisconnectedBrush = SolidColorBrush.Parse("#777777");
    private static readonly IBrush StatusGreen = SolidColorBrush.Parse("#4CAF50");
    private static readonly IBrush StatusGrey = SolidColorBrush.Parse("#555555");

    public string DisplayName { get; }
    public string ItemType { get; }
    public string Identifier { get; }
    public Guid GroupId { get; }
    public bool IsRunning { get; }

    /// <summary>Text color — dimmed for disconnected programs.</summary>
    public IBrush TextBrush => IsRunning ? RunningBrush : DisconnectedBrush;

    /// <summary>Small status dot color.</summary>
    public IBrush StatusDotBrush => IsRunning ? StatusGreen : StatusGrey;

    /// <summary>Whether to show the disconnected indicator.</summary>
    public bool ShowDisconnected => !IsRunning;

    public GroupMemberViewModel(string displayName, string itemType, string identifier,
        Guid groupId, bool isRunning = true)
    {
        DisplayName = displayName;
        ItemType = itemType;
        Identifier = identifier;
        GroupId = groupId;
        IsRunning = isRunning;
    }
}

// ═════════════════════════════════════════════════════════════════════
// PoolItemViewModel
// ═════════════════════════════════════════════════════════════════════

public class PoolItemViewModel
{
    public string DisplayName { get; }
    public string ItemType { get; }
    public string Identifier { get; }

    /// <summary>True when this program is in the IgnoredPrograms list (shown with ⊗ icon).</summary>
    public bool IsIgnored { get; }

    public PoolItemViewModel(string displayName, string itemType, string identifier, bool isIgnored = false)
    {
        DisplayName = displayName;
        ItemType = itemType;
        Identifier = identifier;
        IsIgnored = isIgnored;
    }
}

// ═════════════════════════════════════════════════════════════════════
// ShortcutBindingViewModel
// ═════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents a single configurable shortcut binding in the settings panel.
/// </summary>
public class ShortcutBindingViewModel : ReactiveObject
{
    private string _keyDisplay;
    private string _keyCode;
    private bool _isListening;

    /// <summary>Human-readable action label (e.g. "Volume Up").</summary>
    public string Label { get; }

    /// <summary>Property name on ShortcutConfig (e.g. "VolumeUp").</summary>
    public string ConfigProperty { get; }

    /// <summary>Display text for the currently bound key.</summary>
    public string KeyDisplay
    {
        get => _keyDisplay;
        set => this.RaiseAndSetIfChanged(ref _keyDisplay, value);
    }

    /// <summary>The raw key code string stored in config.</summary>
    public string KeyCode
    {
        get => _keyCode;
        set
        {
            this.RaiseAndSetIfChanged(ref _keyCode, value);
            KeyDisplay = FormatKey(value);
        }
    }

    /// <summary>Whether this binding is currently waiting for a key press.</summary>
    public bool IsListening
    {
        get => _isListening;
        set => this.RaiseAndSetIfChanged(ref _isListening, value);
    }

    public string ButtonText => IsListening ? "..." : KeyDisplay;

    public ShortcutBindingViewModel(string label, string configProperty, string keyCode)
    {
        Label = label;
        ConfigProperty = configProperty;
        _keyCode = keyCode;
        _keyDisplay = FormatKey(keyCode);
    }

    /// <summary>
    /// Formats a key combo string for display.
    /// Input: "Ctrl+Shift+F1" or "F13" or "Ctrl+A"
    /// Output: "Ctrl + Shift + F1" or "F13" or "Ctrl + A"
    /// Strips "Vc" prefix from the key part if present.
    /// </summary>
    public static string FormatKey(string keyCode)
    {
        if (string.IsNullOrEmpty(keyCode)) return "(None)";

        var parts = keyCode.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "(None)";

        // Format each part: modifiers stay as-is, key part strips "Vc" prefix
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("Vc", StringComparison.OrdinalIgnoreCase))
                parts[i] = parts[i][2..];
        }

        return string.Join(" + ", parts);
    }
}
