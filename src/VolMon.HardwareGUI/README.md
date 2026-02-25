# VolMon.HardwareGUI

An Avalonia UI application for configuring and managing hardware devices
connected to the VolMon hardware daemon.

## Features

- **Device list** — shows all detected devices from `hardware.json`
- **Enable/disable toggle** — per-device; changes are written to config and
  picked up live by the hardware daemon
- **Per-device settings** (for devices with displays):
  - Display brightness (active and dimmed)
  - Dim and off timeouts
  - Volume step per dial tick
  - Layout preset selection (dropdown of bundled layouts)
- **Daemon control** — start/stop the hardware daemon process
- **Service management** — install/uninstall the hardware daemon as a system
  service (systemd on Linux, launchd on macOS, Task Scheduler on Windows)
- **Live updates** — watches `~/.config/volmon/*.json` with `FileSystemWatcher`
  for real-time config changes

## Architecture

The HardwareGUI does **not** reference `VolMon.Hardware` directly due to a
SkiaSharp version conflict (Avalonia uses SkiaSharp 2.88.9, the hardware daemon
uses SkiaSharp 3.119.0). Instead, both projects share config models via
`VolMon.Hardware.Models`.

```
VolMon.HardwareGUI
    │
    ├── References: VolMon.Hardware.Models (config DTOs)
    │
    ├── HardwareConfigService    (reads/writes hardware.json + per-device configs)
    │
    ├── MainViewModel            (device list, daemon/service management)
    │   └── DeviceViewModel[]    (one per device, binds to config panel)
    │
    └── MainWindow.axaml         (UI: device list, config panel, toggles)
```

Communication with the hardware daemon is **file-based** — the GUI writes
config files and the daemon picks up changes via `FileSystemWatcher`. There is
no direct IPC between the GUI and the hardware daemon.

## Daemon Management

The GUI can start/stop the daemon and install/uninstall it as a service.

### Detection

When the service is installed, the GUI uses the service manager to check status:

| Platform | Check | Start | Stop |
|---|---|---|---|
| Linux | `systemctl --user is-active volmon-hardware` | `systemctl --user start` | `systemctl --user stop` |
| macOS | `launchctl print gui/{uid}/com.volmon.hardware` | `launchctl load -w` | `launchctl unload -w` |
| Windows | `schtasks /Query /TN "VolMon Hardware Daemon"` | `schtasks /Run` | `schtasks /End` |

When the service is not installed, the GUI falls back to `pgrep`/`pkill` (with
exact name matching via `-x`) on Linux/macOS, and `tasklist`/`taskkill` on
Windows.

### Service Installation

| Platform | Mechanism | Location |
|---|---|---|
| Linux | systemd user unit | `~/.config/systemd/user/volmon-hardware.service` |
| macOS | launchd user agent | `~/Library/LaunchAgents/com.volmon.hardware.plist` |
| Windows | Task Scheduler | Task named "VolMon Hardware Daemon" |

## Config Files

The GUI reads and writes the same config files as the hardware daemon:

| File | Purpose |
|---|---|
| `~/.config/volmon/hardware.json` | Master config: device list + enabled state |
| `~/.config/volmon/beacn-mix-{serial}.json` | Per-device config: brightness, layout, timeouts |

## Running

```bash
# From source
dotnet run --project src/VolMon.HardwareGUI

# Published binary
volmon-hardware-gui
```

The hardware daemon does not need to be running for the GUI to work — the GUI
reads config files directly. However, changes to device settings only take
effect when the daemon is running and picks them up via file watching.
