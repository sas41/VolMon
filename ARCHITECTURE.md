# VolMon Architecture

## System Overview

VolMon is a cross-platform per-application volume grouping utility. It lets
users organize audio streams (and hardware devices) into named groups, each with
a shared volume and mute state. It does **not** create virtual audio devices —
it controls volumes natively through the system audio server.

The codebase is C# / .NET 10, split into four projects.

```
 ┌──────────────┐   ┌──────────────┐
 │  VolMon.CLI  │   │  VolMon.GUI  │
 │ (console app)│   │(Avalonia app)│
 └──────┬───────┘   └──────┬───────┘
        │  Named Pipes IPC │
        └────────┬─────────┘
          ┌──────┴───────┐
          │ VolMon.Daemon│
          │  (bg service)│
          └──────┬───────┘
          ┌──────┴───────┐
          │ VolMon.Core  │
          │  (library)   │
          └──────────────┘
```

**VolMon.Core** is the shared library — models, IPC protocol, config
management, and the audio backend abstraction. **VolMon.Daemon** is a
background service (systemd user unit on Linux) that monitors audio streams,
enforces group volume rules, and hosts the IPC server. **VolMon.CLI** and
**VolMon.GUI** are thin clients that send commands to the daemon over named
pipes.

## Data Flow

### Startup

1. Daemon loads `config.json` from disk (`ConfigManager`).
2. `EnsureIgnoredGroupExistsAsync()` creates the Ignored group if absent
   (`IsIgnored = true`, `Color = "#808080"`).
3. The IPC server starts listening on named pipe `volmon-daemon`.
4. `StreamWatcher` starts the audio backend's monitoring loop.
5. An initial scan (`GetStreamsAsync`, `GetDevicesAsync`) assigns existing
   streams to groups.

### Runtime Event Loop

```
Platform audio events  ──▸  IAudioBackend implementation
                                  │
        C# events (StreamCreated/Removed/Changed, DeviceChanged)
                                  │
                                  ▼
                            StreamWatcher
                       ┌──────────────────────┐
                       │ • Match binary name  │
                       │   against group lists│
                       │ • Assign to default  │
                       │   group if unmatched │
                       │ • Apply volume/mute  │
                       └──────────────────────┘
```

Each backend pushes events as they occur (Linux via `pactl subscribe`, Windows
via COM callbacks, macOS via CoreAudio property listeners). The daemon reacts to
each event — there is no polling on the daemon side.

### IPC Request/Response

```
CLI or GUI                           Daemon
    │                                   │
    ├──connect to named pipe───────────▸│
    ├──send JSON request + newline─────▸│
    │                                   ├── HandleIpcRequestAsync()
    │                                   │   ├── ResolveGroup(request)
    │                                   │   ├── execute command
    │                                   │   └── build IpcResponse
    │◂─receive JSON response + newline──┤
    ├──close connection                 │
```

Each request opens a new pipe connection, sends one JSON line, receives one
JSON line, then disconnects. This keeps the protocol stateless and avoids
connection lifecycle complexity.

## Core Models

### AudioGroup

The central domain object. Each group has:

|Field|Type|Purpose|
|---|---|---|
|`Id`|`Guid`|Primary identifier (auto-assigned)|
|`Name`|`string`|Display name (human-readable, not used as key)|
|`Volume`|`int`|0-100, applied to all member streams/devices|
|`Muted`|`bool`|Mute state|
|`IsDefault`|`bool`|Auto-assign unmatched programs here|
|`Color`|`string?`|Hex color for GUI display (e.g. `"#FF9500"`)|
|`Programs`|`List<string>`|Binary names (case-insensitive match)|
|`Devices`|`List<string>`|Device names (PulseAudio sink/source names)|

**Invariants:**
- Exactly one group or no group has `IsDefault = true`.
- A binary appears in at most one group.

### AudioStream

Represents one per-application audio stream. On Linux this is a PulseAudio
sink-input; on Windows it is a WASAPI audio session; on macOS per-app streams
are not available. Multiple streams can exist for the same binary (e.g. Firefox
creates one per tab on Linux).

Key fields: `Id` (backend-specific — PA index on Linux, PID string on Windows),
`BinaryName`, `ProcessId`, `Volume`, `Muted`, `AssignedGroup` (Guid?).

### AudioDevice

Represents a hardware sink or source. Key fields: `Name` (stable identifier —
PA sink/source name on Linux, device ID on Windows, device UID on macOS),
`Type` (Sink/Source), `Volume`, `Muted`, `AssignedGroup` (Guid?).

Devices are **never** auto-assigned. They must be explicitly added to a group
via CLI/GUI/config.

## Audio Backend

### Interface (`IAudioBackend`)

All platform-specific audio code is behind `IAudioBackend`:

- **Queries:** `GetStreamsAsync()`, `GetDevicesAsync()`
- **Control:** `SetStreamVolumeAsync()`, `SetStreamMuteAsync()`,
  `SetDeviceVolumeAsync()`, `SetDeviceMuteAsync()`
- **Monitoring:** `StartMonitoringAsync()`, `StopMonitoringAsync()`
- **Events:** `StreamCreated`, `StreamRemoved`, `StreamChanged`, `DeviceChanged`

The daemon selects the backend at startup based on the OS
(`PlatformOverlayFactory`-style detection in `Program.cs`).

### PulseAudioBackend (Linux)

Works with both PulseAudio and PipeWire (via its PulseAudio compatibility
layer). No native library bindings — spawns CLI processes and parses output.

**How it works:**
- Spawns `pactl list sink-inputs`, `pactl list sinks`, `pactl list sources` as
  child processes and parses stdout.
- Spawns `pactl subscribe` as a long-lived process for real-time event
  monitoring.
- Volume is converted between PulseAudio's 0-65536 range and VolMon's 0-100
  percentage.

**Binary name resolution** (for identifying which program owns a stream):
1. `/proc/<pid>/comm` — kernel ground truth, most reliable
2. `/proc/<pid>/exe` symlink — fallback
3. PulseAudio `application.process.binary` property
4. PulseAudio `application.name` property

This chain handles cases where PulseAudio's metadata is incorrect or missing
(e.g. sandboxed apps, Flatpak, Electron wrappers).

### WindowsAudioBackend (Windows)

Uses [NAudio](https://github.com/naudio/NAudio) (`NAudio.CoreAudioApi` namespace)
to access the Windows Core Audio API (WASAPI). Supports full per-application
session control and device management.

**Threading:** All COM objects live on a dedicated STA thread
(`VolMon-CoreAudio-STA`) with a `BlockingTaskQueue` message pump. Public methods
marshal work to this thread via `RunOnStaAsync()`, returning `Task`/`Task<T>`.
This is required because Windows Core Audio COM interfaces are
apartment-threaded.

**Per-app sessions:**
- Enumerates `AudioSessionManager.Sessions` on all active render devices.
- Each session is identified by its process ID (PID). The stream ID is the PID
  string — Windows audio sessions are typically 1:1 with processes.
- Volume is mapped between NAudio's 0.0–1.0 float range and VolMon's 0–100 int.
- Process name resolved via `Process.GetProcessById(pid).ProcessName`.
- System-sounds sessions (PID 0) are filtered out.

**Devices:**
- Enumerates both `Render` (Sink) and `Capture` (Source) endpoints via
  `MMDeviceEnumerator.EnumerateAudioEndPoints()`.
- Device `Name` = Windows device ID (stable across reboots). `Description` =
  friendly name (e.g. "Speakers (Realtek Audio)").
- Volume via `AudioEndpointVolume.MasterVolumeLevelScalar`.

**Monitoring:**
- New sessions detected via `AudioSessionManager.OnSessionCreated`. Each new
  session gets a `SessionEventClient` registered for disconnect/expire tracking.
- `StreamRemoved` fired when `OnSessionDisconnected` or
  `OnStateChanged(Expired)` fires on a session.
- Device add/remove/state-change via `IMMNotificationClient` registered on the
  `MMDeviceEnumerator`.

**Limitation:** True exclusive fullscreen (DirectX/Vulkan exclusive mode)
bypasses the Windows compositor entirely. Per-app volume control still works,
but the overlay cannot render above exclusive fullscreen.

### MacOsAudioBackend (macOS)

Uses the CoreAudio HAL (Hardware Abstraction Layer) via direct P/Invoke to
`CoreAudio.framework` and `CoreFoundation.framework`. No external NuGet
dependencies.

**Device-only — per-app streams are NOT supported.** macOS CoreAudio does not
expose per-application audio sessions. Per-app volume control on macOS would
require a virtual audio driver (e.g. BlackHole, Loopback) or a privileged audio
plugin, which is out of scope. Stream methods return empty lists / no-op.

**Devices:**
- Enumerates all devices via `AudioObjectGetPropertyData` on
  `kAudioObjectSystemObject` with `kAudioHardwarePropertyDevices`.
- Determines Sink vs Source by checking `kAudioDevicePropertyStreams` per scope
  (`kAudioObjectPropertyScopeOutput` / `kAudioObjectPropertyScopeInput`).
- Device `Name` = `kAudioDevicePropertyDeviceUID` (stable across reboots).
  `Description` = `kAudioObjectPropertyName` (human-readable).

**Volume control:** Uses the **virtual master volume** selector (`'vmvc'` /
`0x766D7663`). This is a system-synthesized control that manipulates per-channel
volumes while preserving stereo balance. Falls back to per-channel
`kAudioDevicePropertyVolumeScalar` on channels 1–2 if the virtual master is
unavailable.

**Mute:** Uses `kAudioDevicePropertyMute` on element 0 (master).

**Monitoring:**
- Device list changes via `AudioObjectAddPropertyListener` on the system object.
- Per-device volume/mute listeners on both output and input scopes.
- CFString conversion uses a safe managed implementation with `CFStringGetLength`
  / `CFStringGetCharacters` and `Marshal.Copy` (no unsafe code).

## IPC Protocol

**Transport:** .NET named pipes (`System.IO.Pipes`), pipe name `volmon-daemon`.
On Linux the actual socket is at `/tmp/CoreFxPipe_volmon-daemon`.

**Format:** Newline-delimited JSON. One request line, one response line.

**Serialization:** `System.Text.Json` with camelCase naming and enum-as-string
conversion (`IpcSerializer`).

### IpcRequest

|Field|Type|Purpose|
|---|---|---|
Command|(required)|the operation to perform
GroupId|(Guid?)|preferred group identifier (GUI uses this)
GroupName|(string?)|fallback group identifier (CLI uses this)
Volume|(int?)|for set-group-volume
ProgramName|(string?)|for add-program / remove-program
DeviceName|(string?)|for add-device / remove-device
Group|(AudioGroup?)|full group object for add-group
Direction|(string?)|"up"/"down" for move-group
Color|(string?)|hex string for set-group-color
NewName|(string?)|for rename-group
GroupOrder|(List<string>?)|for reorder-groups


### Commands

| Command | Description |
|---|---|
|`list-groups`|Return all configured groups |
|`list-streams`|Return all active audio streams with assignments |
|`list-devices`|Return all hardware audio devices |
|`set-group-olume`|Set volume for a group (0-100) |
|`mute-group`/`unmute-group`|Toggle mute on a group |
|`add-group`|Create a new group |
|`remove-group`|Delete a group |
|`add-program`|Add a program binary to a group |
|`remove-program`|Remove a program from a group |
|`add-device`|Add a device to a group |
|`remove-device`|Remove a device from a group |
|`rename-group`|Change a group's display name |
|`set-group-olor` |Change a group's GUI color |
|`reorder-groups`|Set the display order of groups |
|`status`|Daemon health, counts, uptime |
|`reload`|Re-read config from disk |

## Daemon Internals

### DaemonService

A `BackgroundService` that orchestrates everything:

1. Loads config and runs GUID migration
2. Creates the IPC server and registers command handlers
3. Starts the `StreamWatcher`
4. Handles 18 IPC commands in a `switch` statement

Config changes are persisted to disk via `ConfigManager.SaveAsync()` after
every mutation.

### StreamWatcher

Subscribes to `IAudioBackend` events and maintains group assignments:

- **`AssignStreamToGroup`**: Checks each group's `Programs` list for the
  stream's binary name. If no match, assigns to the default group.
- **`AssignDeviceToGroup`**: Checks each group's `Devices` list. Does NOT
  auto-assign — only matches explicitly listed devices.
- **`ApplyGroupSettingsAsync`**: Sets volume and mute state on all streams
  and devices belonging to a group.

## GUI Architecture

Built with **Avalonia UI 11** using MVVM (ReactiveUI).

### Key Components

**MainViewModel** — root view model:
- Polls daemon via IPC every 250ms (`DispatcherTimer`, visibility-aware)
- Only does a full refresh when daemon status counts change (stream/device/group counts)
- Two-pass stream dedup: Pass 1 determines "best" group per binary name
  (real group wins over Ignored/null), Pass 2 places each binary exactly once
- Exposes `ObservableCollection<GroupColumnViewModel>` for group columns
  and `ObservableCollection<PoolItemViewModel>` for the Applications pool

**GroupColumnViewModel** — one per group column:
- Wraps `AudioGroup` properties (Id, Name, Volume, Muted, Color)
- 80ms volume debounce (CancellationTokenSource-based) to prevent IPC spam
  during slider drags
- Volume/mute changes use `SendQuietAsync` (no refresh) to avoid UI flicker
- Delete and default-toggle use `SendCommandAsync` (triggers refresh)

**MainWindow.axaml.cs** — code-behind for drag-drop and UI interactions:
- Programs and devices are draggable between groups and the Applications pool
- Groups are re-orderable via drag on the group name area
- Color picker: 20-swatch flyout on accent bar click
- Ellipsis menu: Rename (flyout TextBox) and Delete
- `IsInsideInteractiveControl()` guard prevents drag initiation from sliders

### Applications Pool = Ignored Group

The bottom "APPLICATIONS" section is the visual representation of the Ignored
group. It never appears as a group column at the top. Dropping a program back
to Applications moves it to the Ignored group. Dropping from Applications to a
group column removes it from the Ignored group.

### Disconnected Programs

Programs configured in a group but not currently running are shown with a `⊘`
icon and greyed text. The `IsRunning` property is computed by cross-referencing
`AudioGroup.Programs` against active streams.

## Config

**Location:** `~/.config/volmon/config.json` (Linux), resolved via
`Environment.SpecialFolder.ApplicationData`.

**Format:** JSON serialized by `System.Text.Json` with camelCase naming.

**Schema:**
```json
{
  "groups": [
    {
      "id": "a1b2c3d4-...",
      "name": "Default",
      "volume": 100,
      "muted": false,
      "isDefault": true,
      "isIgnored": false,
      "color": "#4A90D9",
      "programs": [],
      "devices": []
    }
  ]
}
```

`ConfigManager` handles load, save, and file-watching (to detect external
edits). The `reload` IPC command triggers a re-read.

## Deployment

### Linux (systemd user service)

```bash
cp install/volmon.service ~/.config/systemd/user/
systemctl --user enable --now volmon
```

The daemon runs in the user session (not as root), which gives it access to the
user's PulseAudio/PipeWire session.


## Future Work

- Diff-based ObservableCollection merging (instead of Clear+Rebuild) to eliminate potential UI flicker
- Drag visual feedback and drop target highlighting
- Per-stream volume overrides within a group
- macOS per-app audio control via virtual audio driver integration
