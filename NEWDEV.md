# New Developer Guide

## Prerequisites

### .NET 10 SDK

Install the .NET 10 SDK from https://dotnet.microsoft.com/download or via your
package manager:

```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-10.0

# Fedora
sudo dnf install dotnet-sdk-10.0

# Arch
sudo pacman -S dotnet-sdk

# Or use the install script
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

Verify:
```bash
dotnet --version
# Should show 10.x.x
```

### PulseAudio CLI tools

Required for the Linux audio backend:

```bash
# Ubuntu/Debian
sudo apt install pulseaudio-utils

# Fedora
sudo dnf install pulseaudio-utils

# Arch
sudo pacman -S libpulse
```

Verify:
```bash
pactl --version
pactl list sink-inputs   # Should list any currently playing audio streams
```

On PipeWire systems, `pactl` is provided by `pipewire-pulse` and works identically.

### libusb (for hardware daemon)

Required for USB device communication:

```bash
# Ubuntu/Debian
sudo apt install libusb-1.0-0-dev

# Fedora
sudo dnf install libusb1-devel

# Arch
sudo pacman -S libusb
```

You also need udev rules to allow non-root USB access. Create
`/etc/udev/rules.d/99-volmon.rules`:

```
# Beacn Mix / Mix Create
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0004", MODE="0666"
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0007", MODE="0666"
```

Then reload: `sudo udevadm control --reload-rules && sudo udevadm trigger`

## Building

```bash
# Clone and build
git clone <repo-url> VolMon
cd VolMon
dotnet restore
dotnet build
```

Build specific projects:
```bash
dotnet build src/VolMon.Core
dotnet build src/VolMon.Daemon
dotnet build src/VolMon.CLI
dotnet build src/VolMon.GUI
dotnet build src/VolMon.Hardware
dotnet build src/VolMon.Hardware.Models
dotnet build src/VolMon.HardwareGUI
```

## Running

You need **two terminals** for development: one for the daemon, one for the CLI/GUI.

### Terminal 1 — Daemon

```bash
dotnet run --project src/VolMon.Daemon
```

The daemon will:
1. Create the config file at `~/.config/volmon/config.json` if it doesn't exist
2. Start the IPC server on named pipe `volmon-daemon`
3. Start monitoring audio streams via `pactl subscribe`

### Terminal 2 — CLI

```bash
# Check connection
dotnet run --project src/VolMon.CLI -- status

# Create a test group
dotnet run --project src/VolMon.CLI -- add-group TestGroup 75
dotnet run --project src/VolMon.CLI -- add-matcher TestGroup binary firefox

# Verify
dotnet run --project src/VolMon.CLI -- groups
dotnet run --project src/VolMon.CLI -- streams
```

### GUI

```bash
dotnet run --project src/VolMon.GUI
```

### Hardware Daemon

Requires a connected USB device and the main daemon running:

```bash
# Terminal 3 — hardware daemon
dotnet run --project src/VolMon.Hardware
```

The hardware daemon will:
1. Connect to the main daemon via named pipe IPC
2. Scan for USB devices every 5 seconds
3. Create per-device config files at `~/.config/volmon/beacn-mix-{serial}.json`
4. Start polling input and updating displays for enabled devices

New devices appear in `~/.config/volmon/hardware.json` as disabled. Enable them
manually or use the hardware GUI.

### Hardware GUI

```bash
dotnet run --project src/VolMon.HardwareGUI
```

The hardware GUI reads/writes the same config files as the hardware daemon.
Changes are picked up live via `FileSystemWatcher` — no restart needed.

## IDE Setup

### VS Code

Install the **C# Dev Kit** extension (or at minimum the **C#** extension by Microsoft).

Recommended `.vscode/settings.json`:
```json
{
  "dotnet.defaultSolution": "VolMon.sln"
}
```

Open the solution:
1. Open the `VolMon/` folder in VS Code
2. The C# extension will detect the `.sln` file
3. Use `Ctrl+Shift+B` to build
4. Use the Run and Debug panel to start individual projects

To debug the daemon:
1. Open `.vscode/launch.json` (create if needed)
2. Add a configuration for `VolMon.Daemon`
3. Set breakpoints in `DaemonService.cs` or `StreamWatcher.cs`

### JetBrains Rider

Open `VolMon.sln` directly. Rider handles multi-project solutions natively.

### Visual Studio (Windows)

Open `VolMon.sln`. Set the startup project to `VolMon.Daemon` or `VolMon.GUI`.

## Project Structure

```
src/
├── VolMon.Core/                # Shared library (no app-specific code)
│   ├── Audio/                  # Audio backend abstraction
│   │   ├── IAudioBackend.cs           # Interface all platforms implement
│   │   ├── AudioStream.cs             # Single app audio stream
│   │   ├── AudioGroup.cs              # Named group with matchers
│   │   ├── StreamMatcher.cs           # Pattern matching rules
│   │   └── Backends/
│   │       ├── PulseAudioBackend.cs   # Linux implementation (pactl)
│   │       ├── WindowsAudioBackend.cs # Windows implementation (WASAPI)
│   │       └── MacOsAudioBackend.cs   # macOS implementation (CoreAudio)
│   ├── Config/
│   │   ├── VolMonConfig.cs            # Config model
│   │   └── ConfigManager.cs           # Load/save/watch
│   └── Ipc/
│       ├── IpcProtocol.cs             # Message types
│       ├── IpcClient.cs               # Client (used by CLI/GUI/Hardware)
│       └── IpcServer.cs               # Server (used by Daemon)
├── VolMon.Daemon/              # Background service
│   ├── Program.cs              # Host builder + DI setup
│   ├── DaemonService.cs        # Main hosted service + IPC handler
│   └── StreamWatcher.cs        # Stream event handler + group matching
├── VolMon.CLI/                 # Command-line tool
│   └── Program.cs              # Argument parsing + IPC commands
├── VolMon.GUI/                 # Avalonia tray app
│   ├── Program.cs              # App builder
│   ├── App.axaml(.cs)          # Application + tray icon setup
│   ├── Views/                  # AXAML windows
│   └── ViewModels/             # MVVM view models
├── VolMon.Hardware.Models/     # Shared config DTOs (no SkiaSharp)
│   ├── HardwareConfig.cs       # HardwareConfig + DeviceEntry
│   └── BeacnMixConfig.cs       # Per-device config for Beacn Mix
├── VolMon.Hardware/            # Hardware daemon
│   ├── Program.cs              # Entry point
│   ├── HardwareBridgeService.cs  # BackgroundService — IPC + DeviceManager
│   ├── DeviceManager.cs        # Scan loop, session lifecycle, config watch
│   ├── DeviceSession.cs        # Per-device IPC bridge (debounce, echo suppress)
│   ├── IDeviceDriver.cs        # Driver interface + DetectedDevice
│   ├── IHardwareController.cs  # Controller interface + event args
│   └── Beacn/                  # Beacn device family
│       ├── BeacnConstants.cs   # USB VID/PID, opcodes, button bits, light IDs
│       ├── BeacnVersion.cs     # Firmware version parsing (packed u32)
│       └── Mix/                # Beacn Mix driver
│           ├── BeacnMixDriver.cs      # USB scan + serial read
│           ├── BeacnMixDevice.cs      # Low-level USB I/O
│           ├── BeacnMixController.cs  # Input poll, display, LEDs, config watch
│           ├── Display/               # Display template system
│           │   ├── DisplayLayout.cs   # Layout model + slot types
│           │   ├── TemplateRenderer.cs  # SkiaSharp rendering → JPEG
│           │   ├── BindingResolver.cs   # {group.*} binding resolution
│           │   └── DefaultLayout.cs     # Hardcoded fallback layout
│           └── Layouts/               # Bundled layout presets (JSON)
│               ├── VolMon_Layout_BeacnMix_default-vertical.json
│               ├── VolMon_Layout_BeacnMix_default-horizontal.json
│               ├── VolMon_Layout_BeacnMix_compact.json
│               └── VolMon_Layout_BeacnMix_arcs.json
└── VolMon.HardwareGUI/         # Avalonia hardware config app
    ├── Program.cs               # App builder
    ├── App.axaml(.cs)           # Application entry
    ├── Views/MainWindow.axaml   # Device list, config panel, daemon/service toggles
    ├── ViewModels/MainViewModel.cs  # Cross-platform daemon management
    └── Services/HardwareConfigService.cs  # Config file I/O, layout listing
```

### SkiaSharp version conflict

`VolMon.Hardware` uses SkiaSharp 3.119.0 for display rendering. `VolMon.GUI`
and `VolMon.HardwareGUI` use Avalonia 11.3.12 which depends on SkiaSharp
2.88.9. These two SkiaSharp versions are ABI-incompatible and cannot coexist in
the same process.

Solution: `VolMon.Hardware.Models` is a separate project containing only config
DTOs (`HardwareConfig`, `DeviceEntry`, `BeacnMixConfig`) with no SkiaSharp
dependency. Both `VolMon.Hardware` and `VolMon.HardwareGUI` reference it.
**`VolMon.HardwareGUI` must never reference `VolMon.Hardware` directly.**

## Key Design Decisions

- **Named Pipes for IPC** — cross-platform (`System.IO.Pipes`), no external dependencies
- **pactl for audio** — works on PulseAudio and PipeWire, avoids native bindings
- **System.Text.Json** — no Newtonsoft, keeps dependencies minimal
- **File-scoped namespaces** — cleaner C# style
- **Async throughout** — all I/O uses async/await
- **Interface-based audio** — swap backends without changing daemon/CLI/GUI code

## Common Tasks

### Adding a new IPC command

1. Add the command string to the switch in `DaemonService.HandleIpcRequestAsync()`
2. Implement the handler method in `DaemonService`
3. Update `IpcRequest`/`IpcResponse` in `IpcProtocol.cs` if new fields are needed
4. Add the CLI argument handling in `CLI/Program.cs`

### Adding a new audio backend

1. Create a new class implementing `IAudioBackend` in `Core/Audio/Backends/`
2. Register it in `Daemon/Program.cs` with a platform check
3. Implement stream listing, volume control, and event monitoring

### Adding a new hardware device

See [src/VolMon.Hardware/README.md](./src/VolMon.Hardware/README.md) for a
detailed guide. Summary:

1. Create a brand folder under `Beacn/` (or new brand under `Hardware/`)
2. Implement `IDeviceDriver` — USB scan, serial read, controller creation
3. Implement `IHardwareController` — USB I/O, input polling, LED control
4. Register the driver in `Program.cs`
5. If the device has a display, implement display rendering
6. Add per-device config class to `VolMon.Hardware.Models`

### Editing the config schema

1. Modify `VolMonConfig.cs` and/or `AudioGroup.cs` / `StreamMatcher.cs`
2. Existing config files will need migration (or just delete and recreate)

### Publishing

```bash
./publish.sh          # Publishes all 5 projects for the current platform
./install/install.sh  # Publish + install + register services
```
