# VolMon

A cross-platform utility for grouping applications by audio stream and
controlling their volumes as a group. Works with PulseAudio/PipeWire on Linux,
Core Audio (WASAPI) on Windows, and CoreAudio HAL on macOS.

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

## Install

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- Linux: PulseAudio or PipeWire (with PulseAudio compatibility)
- Optional: ImageMagick (only needed if regenerating icons from SVG)

### Linux (recommended)

The install script builds, publishes, and sets up everything:

```bash
./install/install.sh
```

This will:
- Publish self-contained binaries for the daemon, GUI, and CLI
- Install them to `~/.local/bin/` (`volmon-daemon`, `volmon-gui`, `volmon`)
- Install and enable the systemd user service (auto-starts the daemon)
- Install the desktop entry and icon for your app launcher

After installation:

```bash
volmon-gui          # Launch the GUI
volmon --help       # CLI usage
systemctl --user status volmon   # Check daemon status
```

> **Note:** Make sure `~/.local/bin` is in your PATH. Most distributions
> include it by default. If not, add `export PATH="$HOME/.local/bin:$PATH"`
> to your shell profile.

### Uninstall

```bash
./install/install.sh --uninstall
```

This stops the daemon, removes the systemd service, desktop entry, icon, and
all installed binaries.

### Development (run from source)

```bash
# Build all projects
dotnet build

# Start the daemon
dotnet run --project src/VolMon.Daemon

# In another terminal, start the GUI
dotnet run --project src/VolMon.GUI

# Or use VS Code — launch the "Daemon+GUI" compound configuration
```

## CLI Usage

```bash
# Check daemon status
volmon status

# Add a default group (catches all unmatched programs)
volmon add-group Default --default

# Add a group with volume
volmon add-group Music 80

# Add programs to the group
volmon add-program Music spotify
volmon add-program Music rhythmbox

# Add a device (e.g. microphone) to a group
volmon add-device Comms alsa_input.pci-0000_00_1f.3.analog-stereo

# List groups
volmon groups

# List active streams and devices
volmon streams
volmon devices

# Set volume
volmon set-volume Music 50

# Mute/unmute
volmon mute Music
volmon unmute Music
```

## GUI

```bash
volmon-gui
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

| Platform | Streams (per-app) | Devices | Monitoring |
|---|---|---|---|
| Linux (PulseAudio) | Full | Full | `pactl subscribe` |
| Linux (PipeWire) | Full | Full | PulseAudio compat layer |
| Windows (WASAPI) | Full | Full | COM callbacks |
| macOS (CoreAudio) | Not available | Full | HAL property listeners |

macOS does not expose per-application audio sessions via CoreAudio. Device-level
volume and mute control works fully. Per-app control would require a virtual
audio driver (e.g. BlackHole) which is out of scope.

## Libraries

| Library | Purpose | Platform |
|---|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 11.2.7 | Cross-platform GUI framework | All |
| [SharpHook](https://github.com/TolikPyl662/SharpHook) 7.1.1 | Global keyboard hooks for hotkeys | All |
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | Windows Core Audio API (WASAPI) access | Windows |

## License

MIT
