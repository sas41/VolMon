# VolMon

A Linux-first utility for grouping applications by audio stream and controlling
their volumes as a group. Works with PulseAudio and PipeWire (via its PulseAudio
compatibility layer).

VolMon does **not** create virtual audio devices or channels. It sets per-stream
volumes natively through the system audio server.

## Features

- **Group applications** by process name or audio class (supports glob patterns)
- **Set group volumes** — all apps in a group share the same volume
- **Auto-apply** — new audio streams are automatically matched and configured
- **CLI + GUI** — manage groups from the terminal or a system tray app
- **Daemon** — background service ensures volumes are applied even without the GUI
- **KDE/GNOME/Windows/macOS** — GUI uses Avalonia UI for cross-platform tray support

## Architecture

See [ARCHITECTURE.md](./ARCHITECTURE.md)

## Quick Start

### 1. Build

```bash
dotnet build
```

### 2. Start the daemon

```bash
dotnet run --project src/VolMon.Daemon
```

Or install as a systemd user service:

```bash
mkdir -p ~/.config/systemd/user
cp install/volmon.service ~/.config/systemd/user/
systemctl --user enable --now volmon
```

### 3. Use the CLI

```bash
# Check daemon status
dotnet run --project src/VolMon.CLI -- status

# Add a default group (catches all unmatched programs)
dotnet run --project src/VolMon.CLI -- add-group Default --default

# Add a group with volume
dotnet run --project src/VolMon.CLI -- add-group Music 80

# Add programs to the group
dotnet run --project src/VolMon.CLI -- add-program Music spotify
dotnet run --project src/VolMon.CLI -- add-program Music rhythmbox

# Add a device (e.g. microphone) to a group
dotnet run --project src/VolMon.CLI -- add-device Comms alsa_input.pci-0000_00_1f.3.analog-stereo

# List groups
dotnet run --project src/VolMon.CLI -- groups

# List active streams and devices
dotnet run --project src/VolMon.CLI -- streams
dotnet run --project src/VolMon.CLI -- devices

# Set volume
dotnet run --project src/VolMon.CLI -- set-volume Music 50

# Mute/unmute
dotnet run --project src/VolMon.CLI -- mute Music
dotnet run --project src/VolMon.CLI -- unmute Music
```

### 4. Use the GUI

```bash
dotnet run --project src/VolMon.GUI
```

The GUI runs as a system tray icon. Click it to open the volume group editor.

## Config

Config is stored at `~/.config/volmon/config.json`:

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

### Group fields

- **programs** — list of process binary names. Matching is case-insensitive.
- **devices** — list of PulseAudio sink/source names (use `volmon devices` to find them).
- **isDefault** — one group should be default. Unrecognized programs go here automatically.
- New hardware devices are **never** auto-assigned. Add them explicitly.

## Platform Support

| Platform | Status |
|---|---|
| Linux (PulseAudio) | Working |
| Linux (PipeWire) | Working (via PulseAudio compat) |
| Windows | Stub (planned) |
| macOS | Stub (low priority) |

## License

MIT
