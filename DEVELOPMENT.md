# Development

## Prerequisites

### .NET 10 SDK

Install from https://dotnet.microsoft.com/download or via your package manager:

```bash
# Arch
sudo pacman -S dotnet-sdk

# Fedora
sudo dnf install dotnet-sdk-10.0

# Or use the install script
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

Verify:

```bash
dotnet --version  # should show 10.x.x
```

### Linux: PulseAudio CLI tools

Required for the Linux audio backend:

```bash
# Arch
sudo pacman -S libpulse

# Fedora
sudo dnf install pulseaudio-utils
```

On PipeWire systems, `pactl` is provided by `pipewire-pulse` and works identically.

Verify:

```bash
pactl --version
pactl list sink-inputs   # lists any currently playing audio streams
```

### Linux: libusb (for hardware daemon)

Required for USB device communication:

```bash
# Arch
sudo pacman -S libusb

# Fedora
sudo dnf install libusb1-devel
```

You also need a udev rule to allow non-root USB access. Create
`/etc/udev/rules.d/99-volmon.rules`:

```
# Beacn Mix / Mix Create
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0004", MODE="0666"
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0007", MODE="0666"
```

Then reload: `sudo udevadm control --reload-rules && sudo udevadm trigger`

---

## Quick start

```bash
git clone <repo-url> VolMon
cd VolMon
dotnet restore
dotnet build
```

Run the daemon and GUI in separate terminals:

```bash
# Terminal 1 — daemon
dotnet run --project src/VolMon.Daemon

# Terminal 2 — GUI
dotnet run --project src/VolMon.GUI

# Terminal 3 — hardware daemon (requires a connected USB device and the main daemon running)
dotnet run --project src/VolMon.Hardware

# Terminal 4 — hardware configuration GUI
dotnet run --project src/VolMon.HardwareGUI
```

Or use the VS Code compound launch configurations (`Daemon+GUI`, `Full Stack`, etc.)
defined in `.vscode/launch.json`.

The daemon will:
1. Create the config file at `~/.config/volmon/config.json` if it doesn't exist
2. Start the IPC server on named pipe `volmon-daemon`
3. Start monitoring audio streams via `pactl subscribe`

The hardware daemon will:
1. Connect to the main daemon via named pipe IPC
2. Scan for USB devices every 5 seconds
3. Create per-device config files at `~/.config/volmon/beacn-mix-{serial}.json`
4. Start polling input and updating displays for enabled devices

New devices appear in `~/.config/volmon/hardware.json` as disabled. Enable them
via the hardware GUI or by editing the file directly.

---

## IDE Setup

### VS Code

Install the **C# Dev Kit** extension (or at minimum the **C#** extension by Microsoft).

The repository includes `.vscode/launch.json` with four individual debug
configurations (GUI, Daemon, Hardware Daemon, Hardware GUI) and three compound
configurations (Daemon+GUI, Daemon+Hardware Daemon, Full Stack).

Use `Ctrl+Shift+B` to build. Set breakpoints in `DaemonService.cs` or
`StreamWatcher.cs` for daemon debugging.

### JetBrains Rider

Open `VolMon.slnx` directly. Rider handles multi-project solutions natively.

### Visual Studio (Windows)

Open `VolMon.slnx`. Set the startup project to `VolMon.Daemon` or `VolMon.GUI`.

---

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
│   │       ├── PulseAudioBackend.cs   # Linux (pactl)
│   │       ├── WindowsAudioBackend.cs # Windows (WASAPI)
│   │       └── MacOsAudioBackend.cs   # macOS (CoreAudio)
│   ├── Config/
│   │   ├── VolMonConfig.cs            # Config model
│   │   └── ConfigManager.cs          # Load/save/watch
│   └── Ipc/
│       ├── IpcProtocol.cs            # Message types
│       ├── IpcClient.cs              # Client (CLI/GUI/Hardware)
│       └── IpcServer.cs              # Server (Daemon)
├── VolMon.Daemon/              # Background service
│   ├── Program.cs              # Host builder + DI setup
│   ├── DaemonService.cs        # Main hosted service + IPC handler
│   └── StreamWatcher.cs        # Stream event handler + group matching
├── VolMon.CLI/                 # Command-line tool
│   └── Program.cs              # Argument parsing + IPC commands
├── VolMon.GUI/                 # Avalonia tray app
│   ├── Program.cs
│   ├── App.axaml(.cs)          # Application + tray icon setup
│   ├── Views/                  # AXAML windows
│   └── ViewModels/             # MVVM view models
├── VolMon.Hardware.Models/     # Shared config DTOs (no SkiaSharp dependency)
│   ├── HardwareConfig.cs       # HardwareConfig + DeviceEntry
│   └── BeacnMixConfig.cs       # Per-device config for Beacn Mix
├── VolMon.Hardware/            # Hardware daemon
│   ├── Program.cs
│   ├── HardwareBridgeService.cs  # BackgroundService — IPC + DeviceManager
│   ├── DeviceManager.cs          # Scan loop, session lifecycle, config watch
│   ├── DeviceSession.cs          # Per-device IPC bridge (debounce, echo suppress)
│   ├── IDeviceDriver.cs          # Driver interface + DetectedDevice
│   ├── IHardwareController.cs    # Controller interface + event args
│   └── Beacn/
│       ├── BeacnConstants.cs     # USB VID/PID, opcodes, button bits, light IDs
│       ├── BeacnVersion.cs       # Firmware version parsing
│       └── Mix/
│           ├── BeacnMixDriver.cs       # USB scan + serial read
│           ├── BeacnMixDevice.cs       # Low-level USB I/O
│           ├── BeacnMixController.cs   # Input poll, display, LEDs, config watch
│           ├── Display/
│           │   ├── DisplayLayout.cs    # Layout model + slot types
│           │   ├── TemplateRenderer.cs # SkiaSharp rendering → JPEG
│           │   ├── BindingResolver.cs  # {group.*} binding resolution
│           │   └── DefaultLayout.cs    # Hardcoded fallback layout
│           └── Layouts/               # Bundled layout presets (JSON)
└── VolMon.HardwareGUI/         # Avalonia hardware config app
    ├── Program.cs
    ├── App.axaml(.cs)
    ├── Views/MainWindow.axaml
    ├── ViewModels/MainViewModel.cs
    └── Services/HardwareConfigService.cs
```

### SkiaSharp version conflict

`VolMon.Hardware` uses SkiaSharp 3.119.0 for display rendering. `VolMon.GUI` and
`VolMon.HardwareGUI` use Avalonia 11.3.12, which depends on SkiaSharp 2.88.x. These
two versions are ABI-incompatible and cannot coexist in the same process.

`VolMon.Hardware.Models` exists specifically to break this dependency: it contains
only config DTOs with no SkiaSharp reference, and is the only project that both
`VolMon.Hardware` and `VolMon.HardwareGUI` share. **`VolMon.HardwareGUI` must never
reference `VolMon.Hardware` directly.**

---

## Key Design Decisions

- **Named pipes for IPC** — cross-platform (`System.IO.Pipes`), no external dependencies
- **pactl for audio** — works on both PulseAudio and PipeWire, avoids native bindings
- **System.Text.Json** — no Newtonsoft, keeps dependencies minimal
- **File-scoped namespaces** — consistent C# style throughout
- **Async throughout** — all I/O uses `async`/`await`
- **Interface-based audio backend** — swap implementations without touching daemon/CLI/GUI code

---

## Common Tasks

### Adding a new IPC command

1. Add the command string to the switch in `DaemonService.HandleIpcRequestAsync()`
2. Implement the handler method in `DaemonService`
3. Update `IpcRequest`/`IpcResponse` in `IpcProtocol.cs` if new fields are needed
4. Add CLI argument handling in `CLI/Program.cs`

### Adding a new audio backend

1. Create a class implementing `IAudioBackend` in `Core/Audio/Backends/`
2. Register it in `Daemon/Program.cs` with a platform check
3. Implement stream listing, volume control, and event monitoring

### Adding a new hardware device

See [src/VolMon.Hardware/README.md](./src/VolMon.Hardware/README.md) for a full
guide. Summary:

1. Create a device folder under `Hardware/` (or a new brand subfolder)
2. Implement `IDeviceDriver` — USB scan, serial read, controller creation
3. Implement `IHardwareController` — USB I/O, input polling, LED control
4. Register the driver in `Program.cs`
5. If the device has a display, implement display rendering
6. Add per-device config class to `VolMon.Hardware.Models`

### Editing the config schema

1. Modify `VolMonConfig.cs` and/or `AudioGroup.cs` / `StreamMatcher.cs`
2. Existing config files will need migration (or delete and recreate)

---

## Publishing

```bash
./publish.sh              # all three platforms (linux-x64, win-x64, osx-x64)
./publish.sh linux-x64    # single platform
```

Output lands in `publish/<rid>/`. Each folder contains the four self-contained
binaries, the platform register script, and the icon asset. See
[README.md](./README.md) for installation instructions.
