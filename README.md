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
- **Hardware control** — physical USB controllers (dials, buttons, displays) for hands-on volume management
- **Hardware GUI** — configure and manage hardware devices, install the hardware daemon as a service
- **KDE/GNOME/Windows/macOS** — GUI uses Avalonia UI for cross-platform tray support

### Supported Hardware

| Device | Dials | Buttons | Display | Status |
|---|---|---|---|---|
| [Beacn Mix](https://www.beacn.com/pages/beacn-mix) | 4 rotary encoders | 4 dial buttons | 800x480 LCD | Fully supported |
| Beacn Mix Create | 4 rotary encoders | 4 dial buttons | 800x480 LCD | Detected, untested |

## Architecture

See [ARCHITECTURE.md](./ARCHITECTURE.md)

## Install

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- Linux: PulseAudio or PipeWire (with PulseAudio compatibility)
- Linux: `libusb-1.0` (for hardware daemon USB communication)
- Optional: ImageMagick (only needed if regenerating icons from SVG)

### Linux (recommended)

The install script builds, publishes, and sets up everything:

```bash
./install/install.sh
```

This will:
- Publish self-contained binaries for all 5 projects (daemon, GUI, CLI, hardware daemon, hardware GUI)
- Install them to `~/.local/bin/` (`volmon-daemon`, `volmon-gui`, `volmon`, `volmon-hardware`, `volmon-hardware-gui`)
- Install and enable systemd user services for both daemons (auto-start)
- Install desktop entries and icon for your app launcher

After installation:

```bash
volmon-gui              # Launch the volume group GUI
volmon --help           # CLI usage
volmon-hardware-gui     # Launch the hardware configuration GUI
systemctl --user status volmon            # Check daemon status
systemctl --user status volmon-hardware   # Check hardware daemon status
```

> **Note:** Make sure `~/.local/bin` is in your PATH. Most distributions
> include it by default. If not, add `export PATH="$HOME/.local/bin:$PATH"`
> to your shell profile.

### Uninstall

```bash
./install/install.sh --uninstall
```

This stops both daemons, removes systemd services, desktop entries, icon, and
all installed binaries.

### Development (run from source)

```bash
# Build all projects
dotnet build

# Start the daemon
dotnet run --project src/VolMon.Daemon

# In another terminal, start the GUI
dotnet run --project src/VolMon.GUI

# Start the hardware daemon (requires a connected USB device)
dotnet run --project src/VolMon.Hardware

# Start the hardware configuration GUI
dotnet run --project src/VolMon.HardwareGUI

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

## Hardware

The hardware daemon (`VolMon.Hardware`) connects to USB controllers and bridges
them to the VolMon daemon via IPC. Each physical dial maps to a volume group,
dial rotation changes volume, and dial button press toggles mute. Devices with
displays show group names, volumes, and mute states.

See [src/VolMon.Hardware/README.md](./src/VolMon.Hardware/README.md) for
architecture details, and
[src/VolMon.Hardware/Beacn/Mix/README.md](./src/VolMon.Hardware/Beacn/Mix/README.md)
for the Beacn Mix USB protocol documentation.

### Hardware Config

Hardware configuration lives alongside the main config:

| File | Purpose |
|---|---|
| `~/.config/volmon/hardware.json` | Master config — lists all known devices, enabled/disabled state |
| `~/.config/volmon/beacn-mix-{serial}.json` | Per-device config — display brightness, layout, volume step, timeouts |

New devices are detected automatically and added to `hardware.json` as
**disabled** by default. Use the Hardware GUI or edit the file directly to
enable them.

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
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 11.3.12 | Cross-platform GUI framework | All |
| [ReactiveUI.Avalonia](https://github.com/reactiveui/ReactiveUI) 11.4.3 | MVVM framework for Avalonia | All |
| [SharpHook](https://github.com/TolikPyl662/SharpHook) 7.1.1 | Global keyboard hooks for hotkeys | All |
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | Windows Core Audio API (WASAPI) access | Windows |
| [LibUsbDotNet](https://github.com/LibUsbDotNet/LibUsbDotNet) 3.0.167-alpha | USB device communication | All |
| [SkiaSharp](https://github.com/mono/SkiaSharp) 3.119.0 | Display rendering (JPEG generation) | Hardware daemon |
