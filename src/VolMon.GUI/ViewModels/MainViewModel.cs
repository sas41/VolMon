using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using ReactiveUI;
using VolMon.Core.Audio;
using VolMon.Core.Ipc;

namespace VolMon.GUI.ViewModels;

public class MainViewModel : ReactiveObject
{
    public static readonly string[] ColorPalette =
    [
        "#FF9500", "#00B4D8", "#E63946", "#2EC4B6",
        "#9B59B6", "#F1C40F", "#1ABC9C", "#E74C3C",
        "#3498DB", "#E67E22"
    ];

    private readonly IpcClient _client = new();
    private Guid? _ignoredGroupId;
    private DaemonStatus? _lastStatus;
    private bool _isRefreshing;

    private string _daemonStatusText = "Checking...";
    private IBrush _daemonStatusColor = Brushes.Gray;
    private string _statusSummary = "";
    private string _newGroupName = "";

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

        _ = RefreshAsync();
    }

    // ── Auto-refresh polling ─────────────────────────────────────────

    /// <summary>
    /// Called by the UI timer (~4×/sec). Only does a full refresh when
    /// the daemon's stream/device/group counts have changed.
    /// </summary>
    public async Task PollAndRefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var resp = await _client.SendAsync(new IpcRequest { Command = "status" });
            if (!resp.Success || resp.Status is null)
            {
                DaemonStatusText = "Not responding";
                DaemonStatusColor = Brushes.Red;
                StatusSummary = "";
                _lastStatus = null;
                return;
            }

            var s = resp.Status;
            if (_lastStatus is null ||
                _lastStatus.ActiveStreams != s.ActiveStreams ||
                _lastStatus.ActiveDevices != s.ActiveDevices ||
                _lastStatus.ConfiguredGroups != s.ConfiguredGroups)
            {
                _lastStatus = s;
                await RefreshDataAsync(s);
            }
            else
            {
                DaemonStatusText = "Running";
                DaemonStatusColor = Brushes.Green;
            }
        }
        catch
        {
            DaemonStatusText = "Not running";
            DaemonStatusColor = Brushes.Red;
            StatusSummary = "Cannot connect to daemon";
            _lastStatus = null;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // ── Full refresh ─────────────────────────────────────────────────

    /// <summary>
    /// Full refresh — fetches status + all data. Called on startup and
    /// after every user-initiated IPC command (add/delete/rename/etc.).
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var statusResp = await _client.SendAsync(new IpcRequest { Command = "status" });
            if (statusResp.Success && statusResp.Status is { } status)
            {
                _lastStatus = status;
                await RefreshDataAsync(status);
            }
            else
            {
                DaemonStatusText = "Not responding";
                DaemonStatusColor = Brushes.Red;
            }
        }
        catch
        {
            DaemonStatusText = "Not running";
            DaemonStatusColor = Brushes.Red;
            StatusSummary = "Cannot connect to daemon";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshDataAsync(DaemonStatus status)
    {
        DaemonStatusText = "Running";
        DaemonStatusColor = Brushes.Green;
        StatusSummary = $"Streams: {status.ActiveStreams}  |  Devices: {status.ActiveDevices}  |  Groups: {status.ConfiguredGroups}";

        // ── Groups (Ignored group hidden from columns)
        var groupsResp = await _client.SendAsync(new IpcRequest { Command = "list-groups" });
        Groups.Clear();
        _ignoredGroupId = null;

        if (groupsResp.Groups is not null)
        {
            var ci = 0;
            foreach (var g in groupsResp.Groups)
            {
                if (g.IsIgnored) { _ignoredGroupId = g.Id; continue; }
                var color = g.Color ?? ColorPalette[ci % ColorPalette.Length];
                Groups.Add(new GroupColumnViewModel(g, color, SendCommandAsync, SendQuietAsync));
                ci++;
            }
        }

        // ── Streams → group members or Applications pool
        // A single program (e.g. Firefox) can have multiple PulseAudio streams
        // (one per tab). We deduplicate by binary name so each program appears
        // only once — either in its assigned group or in the Applications pool.
        //
        // Two-pass approach: first collect per-binary "best" assignment (a real
        // group wins over Ignored/unassigned), then place each binary exactly once.
        var streamsResp = await _client.SendAsync(new IpcRequest { Command = "list-streams" });
        UnassignedPrograms.Clear();

        if (streamsResp.Streams is not null)
        {
            var bestAssignment = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in streamsResp.Streams)
            {
                var isIgnoredOrNull = s.AssignedGroup is null ||
                    (_ignoredGroupId.HasValue && s.AssignedGroup == _ignoredGroupId.Value);

                if (!bestAssignment.TryGetValue(s.BinaryName, out var existing))
                {
                    bestAssignment[s.BinaryName] = isIgnoredOrNull ? null : s.AssignedGroup;
                }
                else if (existing is null && !isIgnoredOrNull)
                {
                    bestAssignment[s.BinaryName] = s.AssignedGroup;
                }
            }

            foreach (var (binary, groupId) in bestAssignment)
            {
                if (groupId is { } assignedId)
                {
                    var gvm = Groups.FirstOrDefault(g => g.Id == assignedId);
                    gvm?.Members.Add(new GroupMemberViewModel(
                        binary, "program", binary, assignedId, isRunning: true));
                }
                else
                {
                    UnassignedPrograms.Add(new PoolItemViewModel(binary, "program", binary));
                }
            }
        }

        // ── Disconnected programs: configured in a group but not currently running
        foreach (var gvm in Groups)
        {
            var running = gvm.Members
                .Where(m => m.ItemType == "program")
                .Select(m => m.Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var prog in gvm.ConfiguredPrograms)
            {
                if (!running.Contains(prog))
                {
                    gvm.Members.Add(new GroupMemberViewModel(
                        prog, "program", prog, gvm.Id, isRunning: false));
                }
            }
        }

        // ── Devices → group members or device pools
        var devResp = await _client.SendAsync(new IpcRequest { Command = "list-devices" });
        UnassignedInputs.Clear();
        UnassignedOutputs.Clear();

        if (devResp.Devices is not null)
        {
            foreach (var d in devResp.Devices)
            {
                var display = d.Description ?? d.Name;
                var isInput = d.Type.Equals("source", StringComparison.OrdinalIgnoreCase);
                var itemType = isInput ? "input-device" : "output-device";

                if (d.AssignedGroup is { } devGroupId)
                {
                    var gvm = Groups.FirstOrDefault(g => g.Id == devGroupId);
                    gvm?.Members.Add(new GroupMemberViewModel(
                        display, itemType, d.Name, devGroupId, isRunning: true));
                }
                else
                {
                    (isInput ? UnassignedInputs : UnassignedOutputs)
                        .Add(new PoolItemViewModel(display, itemType, d.Name));
                }
            }
        }
    }

    // ── Group actions ────────────────────────────────────────────────

    private async Task AddGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await _client.SendAsync(new IpcRequest
        {
            Command = "add-group",
            Group = new AudioGroup { Name = name, Volume = 100 }
        });

        NewGroupName = "";
        await RefreshAsync();
    }

    public async Task RenameGroupAsync(Guid groupId, string newName)
    {
        await _client.SendAsync(new IpcRequest
        {
            Command = "rename-group",
            GroupId = groupId,
            NewName = newName
        });
        await RefreshAsync();
    }

    // ── Drag-drop ────────────────────────────────────────────────────

    public async Task AssignToGroupAsync(Guid groupId, string itemType, string id)
    {
        var cmd = itemType == "program" ? "add-program" : "add-device";
        var req = itemType == "program"
            ? new IpcRequest { Command = cmd, GroupId = groupId, ProgramName = id }
            : new IpcRequest { Command = cmd, GroupId = groupId, DeviceName = id };

        await _client.SendAsync(req);
        await RefreshAsync();
    }

    public async Task ReturnToPoolAsync(Guid currentGroupId, string itemType, string id)
    {
        if (itemType == "program" && _ignoredGroupId.HasValue)
        {
            await _client.SendAsync(new IpcRequest
            {
                Command = "add-program",
                GroupId = _ignoredGroupId.Value,
                ProgramName = id
            });
        }
        else if (itemType == "program")
        {
            await _client.SendAsync(new IpcRequest
            {
                Command = "remove-program",
                GroupId = currentGroupId,
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
        await RefreshAsync();
    }

    public async Task ReorderGroupAsync(Guid sourceId, Guid targetId)
    {
        var src = Groups.FirstOrDefault(g => g.Id == sourceId);
        var tgt = Groups.FirstOrDefault(g => g.Id == targetId);
        if (src is null || tgt is null || src == tgt) return;

        Groups.Move(Groups.IndexOf(src), Groups.IndexOf(tgt));

        var order = Groups.Select(g => g.Id.ToString()).ToList();
        if (_ignoredGroupId.HasValue) order.Add(_ignoredGroupId.Value.ToString());

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

    // ── IPC helpers ──────────────────────────────────────────────────

    private async Task SendCommandAsync(IpcRequest request)
    {
        await _client.SendAsync(request);
        await RefreshAsync();
    }

    private async Task SendQuietAsync(IpcRequest request)
    {
        try { await _client.SendAsync(request); }
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
    private string _colorHex;
    private IBrush _colorBrush;
    private CancellationTokenSource? _volumeDebounce;

    public Guid Id { get; }
    public string Name { get; }
    public string VolumeText => $"{_volume}%";

    /// <summary>Programs configured in this group (for disconnected indicators).</summary>
    public IReadOnlyList<string> ConfiguredPrograms { get; }

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

            _volumeDebounce?.Cancel();
            _volumeDebounce = new CancellationTokenSource();
            var ct = _volumeDebounce.Token;
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
            _ = _sendQuiet(new IpcRequest
            {
                Command = value ? "mute-group" : "unmute-group",
                GroupId = Id
            });
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault == value) return;
            this.RaiseAndSetIfChanged(ref _isDefault, value);
            if (value)
            {
                _ = _sendCommand(new IpcRequest
                {
                    Command = "set-default-group",
                    GroupId = Id
                });
            }
        }
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

    public PoolItemViewModel(string displayName, string itemType, string identifier)
    {
        DisplayName = displayName;
        ItemType = itemType;
        Identifier = identifier;
    }
}
