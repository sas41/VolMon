# AGENTS.md — VolMon Project Guide

## Overview

VolMon is a Linux-first (with future Windows/macOS support) utility for grouping
applications by audio stream and controlling their volumes as a group. It does
**not** create virtual audio devices or channels — it sets per-stream volumes
natively through the system audio server.

The entire codebase is C# / .NET 10.

## Architecture

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

### Projects

| Project | Type | Description |
|---|---|---|
| `VolMon.Core` | Class library (`net10.0`) | Audio backend abstraction, config management, IPC protocol, group logic |
| `VolMon.Daemon` | Worker service (`net10.0`) | Background service that watches for new audio streams and applies group volume rules. Exposes a Named Pipes IPC server. |
| `VolMon.CLI` | Console app (`net10.0`) | Command-line interface. Sends IPC commands to the daemon. |
| `VolMon.GUI` | Avalonia app (`net10.0`) | System tray application with a popup/window for editing groups. Sends IPC commands to the daemon. |

### Directory Layout

```
VolMon/
├── AGENTS.md
├── README.md
├── NEWDEV.md
├── VolMon.sln
├── src/
│   ├── VolMon.Core/
│   │   ├── VolMon.Core.csproj
│   │   ├── Audio/
│   │   │   ├── IAudioBackend.cs        # Interface for platform backends
│   │   │   ├── AudioStream.cs          # Represents a single app audio stream
│   │   │   ├── AudioDevice.cs          # Represents a hardware sink/source
│   │   │   ├── AudioGroup.cs           # A named group with volume + programs + devices
│   │   │   └── Backends/
│   │   │       ├── PulseAudioBackend.cs    # Linux: pactl/wpctl
│   │   │       ├── WindowsAudioBackend.cs  # Windows: stub
│   │   │       └── MacOsAudioBackend.cs    # macOS: stub
│   │   ├── Config/
│   │   │   ├── VolMonConfig.cs         # Config model (serialized to JSON)
│   │   │   └── ConfigManager.cs        # Load/save/watch config file
│   │   └── Ipc/
│   │       ├── IpcProtocol.cs          # Message types and serialization
│   │       ├── IpcClient.cs            # Named pipe client (CLI/GUI use this)
│   │       └── IpcServer.cs            # Named pipe server (Daemon hosts this)
│   ├── VolMon.Daemon/
│   │   ├── VolMon.Daemon.csproj
│   │   ├── Program.cs
│   │   ├── DaemonService.cs            # Hosted service: stream watcher + IPC
│   │   └── StreamWatcher.cs            # Monitors pactl subscribe for events
│   ├── VolMon.CLI/
│   │   ├── VolMon.CLI.csproj
│   │   └── Program.cs
│   └── VolMon.GUI/
│       ├── VolMon.GUI.csproj
│       ├── Program.cs
│       ├── App.axaml
│       ├── App.axaml.cs
│       ├── Views/
│       │   └── MainWindow.axaml
│       │   └── MainWindow.axaml.cs
│       └── ViewModels/
│           └── MainViewModel.cs
└── install/
    └── volmon.service                  # systemd user unit file
```

## Group Model

Groups are the central concept. Each group has:
- **name** — unique identifier
- **volume** — 0-100, applied to all members
- **muted** — boolean
- **isDefault** — if true, newly detected programs auto-assign here
- **programs** — list of process binary names (e.g. `["spotify", "firefox"]`)
- **devices** — list of audio device names (e.g. PulseAudio sink/source names)

### Assignment rules

- When a new **program** audio stream appears, the daemon checks each group's
  `programs` list. If the binary name matches, that group's volume is applied.
  If no group claims it, it goes to the **default group** (the one with
  `isDefault: true`).
- When a new **hardware device** appears, it is **not** auto-assigned to any
  group. Devices must be explicitly added to a group via CLI/GUI/config.
- Programs can only be in one group at a time. Adding a program to a group
  removes it from any other group.

## Audio Backend

### Linux (Primary)

Uses `pactl` (PulseAudio CLI) which works on both PulseAudio and PipeWire
(via its PulseAudio compatibility layer).

Key commands:
- `pactl list sink-inputs` — list all per-app audio streams
- `pactl list sinks` / `pactl list sources` — list hardware devices
- `pactl set-sink-input-volume <index> <volume>` — set stream volume
- `pactl set-sink-volume <name> <volume>` — set device volume
- `pactl set-source-volume <name> <volume>` — set mic volume
- `pactl subscribe` — watch for stream/device create/remove/change events

The backend spawns these as child processes and parses stdout.

### Windows (Stub)

Will use Windows Core Audio API (`IAudioSessionManager2`) via COM interop
or the `NAudio` NuGet package.

### macOS (Stub)

Will use CoreAudio CLI tools. Lowest priority.

## Config File

Location (cross-platform):
- Linux: `~/.config/volmon/config.json`
- Windows: `%APPDATA%\volmon\config.json`
- macOS: `~/Library/Application Support/volmon/config.json`

Resolved via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`.

### Config Schema

```json
{
  "groups": [
    {
      "name": "Default",
      "volume": 100,
      "muted": false,
      "isDefault": true,
      "programs": [],
      "devices": []
    },
    {
      "name": "Music",
      "volume": 80,
      "muted": false,
      "isDefault": false,
      "programs": ["spotify", "rhythmbox"],
      "devices": []
    },
    {
      "name": "Comms",
      "volume": 90,
      "muted": false,
      "isDefault": false,
      "programs": ["discord", "telegram-desktop"],
      "devices": ["alsa_input.pci-0000_00_1f.3.analog-stereo"]
    }
  ]
}
```

## IPC Protocol

Communication between CLI/GUI and Daemon uses **Named Pipes**
(`System.IO.Pipes`), which work on Linux, Windows, and macOS.

Pipe name: `volmon-daemon`

Messages are newline-delimited JSON. Each request gets exactly one response.

### Commands

| Command | Description |
|---|---|
| `list-groups` | List all configured groups |
| `list-streams` | List all active audio streams and their group assignments |
| `list-devices` | List all hardware audio devices |
| `set-group-volume <name> <0-100>` | Set volume for a group |
| `mute-group <name>` | Mute a group |
| `unmute-group <name>` | Unmute a group |
| `add-group <json>` | Add a new group |
| `remove-group <name>` | Remove a group |
| `add-program <group> <binary>` | Add a program to a group |
| `remove-program <group> <binary>` | Remove a program from a group |
| `add-device <group> <device-name>` | Add a device to a group |
| `remove-device <group> <device-name>` | Remove a device from a group |
| `status` | Daemon health, stream count, device count |
| `reload` | Re-read config from disk |

## GUI

Built with **Avalonia UI**. Runs as a system tray icon.

- On KDE Plasma: uses StatusNotifierItem D-Bus protocol (native tray support)
- On GNOME: uses AppIndicator or similar extension
- On Windows/macOS: native tray support via Avalonia

Clicking the tray icon opens a popup/window to:
- View active streams and their group assignments
- View detected audio devices
- Create/edit/delete groups
- Adjust group volumes with sliders
- Mute/unmute groups

## Daemon

Runs as a **systemd user service** on Linux. Install with:
```bash
cp install/volmon.service ~/.config/systemd/user/
systemctl --user enable --now volmon
```

The daemon:
1. Loads config from disk
2. Starts the IPC server (named pipe)
3. Starts the stream watcher (`pactl subscribe`)
4. When a new stream appears, checks group program lists then assigns to
   default group if unmatched
5. When a new device appears, checks group device lists (does NOT auto-assign)
6. When a group volume changes (via IPC), applies to all assigned streams
   and devices

## Coding Conventions

- Target `net10.0`
- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Use file-scoped namespaces
- Async/await for all I/O operations
- `System.Text.Json` for all serialization (no Newtonsoft)
- No third-party dependencies in Core except what's necessary
- Avalonia and its packages are the only large dependency (GUI only)
- All platform-specific code is behind the `IAudioBackend` interface
- Prefer spawning CLI tools (`pactl`) over native library bindings
