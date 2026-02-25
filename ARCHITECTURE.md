# VolMon Architecture

## System Overview

VolMon is a cross-platform per-application volume grouping utility. It lets
users organize audio streams (and hardware devices) into named groups, each with
a shared volume and mute state. It does **not** create virtual audio devices —
it controls volumes natively through the system audio server.

The codebase is C# / .NET 10, split into seven projects.

```
 ┌──────────────┐   ┌──────────────┐    ┌──────────────────┐
 │  VolMon.CLI  │   │  VolMon.GUI  │    │VolMon.HardwareGUI│
 │ (console app)│   │(Avalonia app)│    │  (Avalonia app)  │
 └──────┬───────┘   └──────┬───────┘    └────────┬─────────┘
        │  Named Pipes IPC │                     │ File I/O
        └────────┬─────────┘                     │ (hardware.json,
          ┌──────┴───────┐                       │  per-device configs)
          │ VolMon.Daemon│              ┌────────┴─────────┐
          │  (bg service)│◂─ IPC ──────▸│ VolMon.Hardware  │
          └──────┬───────┘              │(hardware daemon) │
          ┌──────┴───────┐              └────────┬─────────┘
          │ VolMon.Core  │              ┌────────┴─────────┐
          │  (library)   │              │VolMon.Hardware   │
          └──────────────┘              │      .Models     │
                                        │ (shared config)  │
                                        └──────────────────┘
```

**VolMon.Core** is the shared library — models, IPC protocol, config
management, and the audio backend abstraction. **VolMon.Daemon** is a
background service (systemd user unit on Linux) that monitors audio streams,
enforces group volume rules, and hosts the IPC server. **VolMon.CLI** and
**VolMon.GUI** are thin clients that communicate with the daemon over named
pipes using a persistent duplex connection.

**VolMon.Hardware** is a separate hardware daemon that communicates with USB
controllers (e.g. Beacn Mix) and bridges physical input (dials, buttons) to
daemon commands via the same IPC pipe. **VolMon.HardwareGUI** is an Avalonia
configuration app for managing hardware devices. **VolMon.Hardware.Models** is
a shared library containing config DTOs used by both Hardware and HardwareGUI
(it exists to avoid a SkiaSharp version conflict — see
[Hardware Models README](./src/VolMon.Hardware.Models/README.md)).

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
                       │ • Raise StateChanged │
                       │   (100ms debounce)   │
                       └──────────────────────┘
                                  │
                                  ▼
                       DaemonService broadcasts
                       state-changed event to
                       all connected IPC clients
```

Each backend pushes events as they occur (Linux via `pactl subscribe`, Windows
via COM callbacks, macOS via CoreAudio property listeners). The daemon reacts to
each event — there is no polling on the daemon side. `StreamWatcher` debounces
its `StateChanged` event (100ms) to coalesce rapid-fire audio events (e.g.
applying volume to multiple streams) into a single broadcast.

### IPC Protocol (Duplex)

```
CLI                                  Daemon
    │                                   │
    ├──connect to named pipe───────────▸│
    ├──send IpcMessage(request)────────▸│
    │                                   ├── HandleIpcRequestAsync()
    │◂─IpcMessage(response)─────────────┤
    ├──dispose (close connection)       │

GUI                                  Daemon
    │                                   │
    ├──connect to named pipe───────────▸│
    ├──send IpcMessage(request:status)─▸│
    │◂─IpcMessage(response:full state)──┤
    │                                   │
    │◂─IpcMessage(event:state-changed)──┤  ← push on any mutation
    │◂─IpcMessage(event:state-changed)──┤  ← push on audio events
    │                                   │
    ├──send IpcMessage(request)────────▸│  ← user actions (volume, etc.)
    │◂─IpcMessage(response)─────────────┤
    │  ... (connection stays open) ...  │
```

The protocol uses persistent duplex connections. Each client connects once and
the connection stays open for the lifetime of the client. The daemon pushes
`state-changed` events to all connected clients whenever state mutates (IPC
commands, audio backend events, config changes). This eliminates polling.

**CLI** connects, sends one request, receives the response, then disconnects.
Push events that arrive during the brief connection are ignored.

**GUI** connects at startup and stays connected. It receives push events and
updates the UI reactively. If the connection drops, it auto-reconnects after 3
seconds.

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

**Format:** Newline-delimited JSON. Each message is a single JSON line
containing an `IpcMessage` envelope.

**Serialization:** `System.Text.Json` with camelCase naming and enum-as-string
conversion (`IpcSerializer`).

### IpcMessage Envelope

Every message on the wire is an `IpcMessage`:

|Field|Type|Purpose|
|---|---|---|
|`Type`|`IpcMessageType`|`request`, `response`, or `event`|
|`Id`|`Guid`|Correlation ID (set on requests, echoed on responses)|
|`Request`|`IpcRequest?`|Populated for `request` messages|
|`Response`|`IpcResponse?`|Populated for `response` messages|
|`Event`|`IpcEvent?`|Populated for `event` messages|

### IpcRequest

|Field|Type|Purpose|
|---|---|---|
|Command|(required)|the operation to perform|
|GroupId|(Guid?)|preferred group identifier (GUI uses this)|
|GroupName|(string?)|fallback group identifier (CLI uses this)|
|Volume|(int?)|for set-group-volume|
|ProgramName|(string?)|for add-program / remove-program|
|DeviceName|(string?)|for add-device / remove-device|
|Group|(AudioGroup?)|full group object for add-group|
|Direction|(string?)|"up"/"down" for move-group|
|Color|(string?)|hex string for set-group-color|
|NewName|(string?)|for rename-group|
|GroupOrder|(List\<string\>?)|for reorder-groups|

### IpcEvent

|Field|Type|Purpose|
|---|---|---|
|`Name`|`string`|Event type (currently only `"state-changed"`)|
|`Groups`|`List<AudioGroup>?`|Full group list snapshot|
|`Streams`|`List<AudioStreamInfo>?`|Full stream list snapshot|
|`Devices`|`List<AudioDeviceInfo>?`|Full device list snapshot|
|`Status`|`DaemonStatus?`|Daemon health info|

The daemon broadcasts a `state-changed` event containing a full state snapshot
after every mutation command and whenever the `StreamWatcher` detects audio
backend changes. This is the sole mechanism for GUI state updates — the GUI
does not poll.

### Commands

| Command | Description |
|---|---|
|`list-groups`|Return all configured groups |
|`list-streams`|Return all active audio streams with assignments |
|`list-devices`|Return all hardware audio devices |
|`set-group-volume`|Set volume for a group (0-100) |
|`mute-group`/`unmute-group`|Toggle mute on a group |
|`add-group`|Create a new group |
|`remove-group`|Delete a group |
|`add-program`|Add a program binary to a group |
|`remove-program`|Remove a program from a group |
|`add-device`|Add a device to a group |
|`remove-device`|Remove a device from a group |
|`rename-group`|Change a group's display name |
|`set-group-color`|Change a group's GUI color |
|`reorder-groups`|Set the display order of groups |
|`status`|Daemon health, counts, uptime, full state |
|`reload`|Re-read config from disk |

## Daemon Internals

### DaemonService

A `BackgroundService` that orchestrates everything:

1. Loads config and runs GUID migration
2. Creates the `IpcDuplexServer` and registers command handlers
3. Starts the `StreamWatcher`
4. Handles 18 IPC commands in a `switch` statement
5. Broadcasts `state-changed` events to all connected clients after mutations
6. Debounces config saves (500ms) for high-frequency operations (volume, mute,
   color) to avoid excessive disk I/O during slider drags

### IpcDuplexServer

The duplex named pipe server that replaced the old one-shot `IpcServer`:

- Accepts multiple concurrent persistent connections
  (`MaxAllowedServerInstances`)
- Each client gets a dedicated read loop running on a background thread
- Command handler execution is serialized via `SemaphoreSlim(1,1)` — the daemon
  handler is not thread-safe
- Per-client write serialization via `SemaphoreSlim` prevents broadcast and
  response writes from interleaving
- `BroadcastAsync(IpcEvent)` sends an event to all connected clients; clients
  that fail to receive are silently disconnected

### StreamWatcher

Subscribes to `IAudioBackend` events and maintains group assignments:

- **`AssignStreamToGroup`**: Checks each group's `Programs` list for the
  stream's binary name. If no match, assigns to the default group.
- **`AssignDeviceToGroup`**: Checks each group's `Devices` list. Does NOT
  auto-assign — only matches explicitly listed devices.
- **`ApplyGroupSettingsAsync`**: Sets volume and mute state on all streams
  and devices belonging to a group.
- **`StateChanged`**: Debounced event (100ms) raised on stream/device changes.
  The daemon subscribes to this to broadcast state updates to IPC clients.

## GUI Architecture

Built with **Avalonia UI 11** using MVVM (ReactiveUI).

### Key Components

**MainViewModel** — root view model:
- Connects to the daemon via `IpcDuplexClient` on startup (persistent
  connection with auto-reconnect on disconnect)
- Receives push `state-changed` events from the daemon — no polling
- Reconciles `ObservableCollection` contents via key-based diff-merge
  (`CollectionReconciler`) to avoid UI flicker from clear/re-add
- Two-pass stream dedup: Pass 1 determines "best" group per binary name
  (real group wins over Ignored/null), Pass 2 places each binary exactly once
- Exposes `ObservableCollection<GroupColumnViewModel>` for group columns
  and `ObservableCollection<PoolItemViewModel>` for the Applications pool

**IpcDuplexClient** — shared by GUI and CLI:
- Maintains a persistent named pipe connection with a background read loop
- `SendAsync(request)` sends a request and awaits the correlated response
  (matched by `IpcMessage.Id`)
- `SendFireAndForgetAsync(request)` sends without awaiting a response (used
  for high-frequency volume/mute changes where the push event suffices)
- `EventReceived` event delivers push notifications from the daemon
- `Disconnected` event fires when the connection drops

**GroupColumnViewModel** — one per group column:
- Wraps `AudioGroup` properties (Id, Name, Volume, Muted, Color)
- 80ms volume debounce (CancellationTokenSource-based) to prevent IPC spam
  during slider drags
- Volume/mute changes use `SendFireAndForgetAsync` (fire-and-forget — the
  daemon push event updates the UI)
- `UpdateFrom()` patches properties in place from pushed state, skipping
  volume while a debounce is pending and muted while a command is in-flight
- Delete and default-toggle use `SendAsync` (waits for response)

**MainWindow.axaml.cs** — code-behind for drag-drop and UI interactions:
- Programs and devices are draggable between groups and the Applications pool
- Groups are re-orderable via drag on the group name area
- Color picker: 20-swatch flyout on accent bar click
- Ellipsis menu: Rename (flyout TextBox) and Delete
- `IsInsideInteractiveControl()` guard prevents drag initiation from sliders
- No polling timer — state updates are purely event-driven

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
edits). The `reload` IPC command triggers a re-read. High-frequency mutations
(volume, mute, color) use debounced saves (500ms) to coalesce rapid changes
into a single disk write.

## Deployment

### Linux (systemd user service)

```bash
cp install/volmon.service ~/.config/systemd/user/
systemctl --user enable --now volmon
```

The daemon runs in the user session (not as root), which gives it access to the
user's PulseAudio/PipeWire session.


## Hardware Daemon Architecture

The hardware daemon (`VolMon.Hardware`) runs as a separate process alongside
the main VolMon daemon. It manages physical USB controllers, bridging hardware
input to daemon IPC commands.

### Design Principles

- **One daemon, all devices** — a single hardware daemon process manages every
  connected USB controller.
- **Crash isolation** — each device runs in its own `DeviceSession` (a
  background `Task`). If one device crashes, the session is marked `Faulted`
  and other devices continue running. Faulted sessions are automatically
  restarted on the next scan cycle.
- **No firmware uploads** — the daemon only reads input and sends display/LED
  commands using the device's existing USB protocol. It never writes firmware.

### Component Chain

```
USB Device  ◂──▸  IDeviceDriver  ──▸  IHardwareController  ──▸  DeviceSession  ──▸  DeviceManager
                  (scan/detect)       (USB I/O, input,         (IPC bridge,       (lifecycle,
                                       display, LEDs)           debounce,          config watch,
                                                                echo suppress)     scan loop)
```

1. **IDeviceDriver** — scans USB bus for devices of a specific type, reads
   serial numbers, creates controller instances. One driver per device family
   (e.g. `BeacnMixDriver`).
2. **IHardwareController** — manages a single physical device: opens USB,
   polls for input, renders display, controls LEDs. Raises `DialRotated` and
   `ButtonPressed` events.
3. **DeviceSession** — wraps a controller with IPC bridge logic. Converts dial
   events to `set-group-volume` commands, button events to `mute-group`/
   `unmute-group` commands. Applies 30ms dial debounce and 200ms echo
   suppression.
4. **DeviceManager** — orchestrates the scan loop, reconciles detected devices
   against `hardware.json` config, starts/stops sessions, watches config for
   live enable/disable changes.

### HardwareBridgeService

The top-level `BackgroundService` that wires everything together:
- Connects to the VolMon daemon via `IpcDuplexClient`
- Creates `DeviceManager` with all registered `IDeviceDriver` instances
- Subscribes to daemon `state-changed` events and broadcasts them to all
  active device sessions

### Config

| File | Contents |
|---|---|
| `~/.config/volmon/hardware.json` | Master config: known devices, enabled/disabled state, scan interval |
| `~/.config/volmon/beacn-mix-{serial}.json` | Per-device: display brightness, dim/off timeouts, volume step, layout |

Both files are watched with `FileSystemWatcher` for live hot-reload. Enabling
a device in the GUI (or editing `hardware.json`) triggers an immediate
scan-and-reconcile cycle.

### Display System (Beacn Mix)

The Beacn Mix has an 800x480 LCD display driven by a JSON-based template
system:

- **DisplayLayout** — JSON file defining visual slots (text, bars, arcs,
  images, rectangles) with data bindings like `{group.name}`,
  `{group.volume}`, `{group.muted}`.
- **TemplateRenderer** — renders the layout to a JPEG image using SkiaSharp.
- **BindingResolver** — resolves `{group.*}` bindings against the current
  `GroupDisplayState[]`.
- Layout resolution: bundled `Layouts/` dir first, then config dir
  (`~/.config/volmon/`), then hardcoded default. No files outside these
  directories are read.

Display updates are signal-based (`ManualResetEventSlim`) — the display is
only re-rendered when state changes, not on a timer.

### Adding a New Device

See [src/VolMon.Hardware/README.md](./src/VolMon.Hardware/README.md) for a
step-by-step guide to adding support for a new hardware device.

## Future Work

- Drag visual feedback and drop target highlighting
- Per-stream volume overrides within a group
- macOS per-app audio control via virtual audio driver integration
