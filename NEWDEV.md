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
├── VolMon.Core/           # Shared library (no app-specific code)
│   ├── Audio/             # Audio backend abstraction
│   │   ├── IAudioBackend.cs       # Interface all platforms implement
│   │   ├── AudioStream.cs         # Single app audio stream
│   │   ├── AudioGroup.cs          # Named group with matchers
│   │   ├── StreamMatcher.cs       # Pattern matching rules
│   │   └── Backends/
│   │       ├── PulseAudioBackend.cs   # Linux implementation (pactl)
│   │       ├── WindowsAudioBackend.cs # Stub
│   │       └── MacOsAudioBackend.cs   # Stub
│   ├── Config/
│   │   ├── VolMonConfig.cs        # Config model
│   │   └── ConfigManager.cs       # Load/save/watch
│   └── Ipc/
│       ├── IpcProtocol.cs         # Message types
│       ├── IpcClient.cs           # Client (used by CLI/GUI)
│       └── IpcServer.cs           # Server (used by Daemon)
├── VolMon.Daemon/         # Background service
│   ├── Program.cs         # Host builder + DI setup
│   ├── DaemonService.cs   # Main hosted service + IPC handler
│   └── StreamWatcher.cs   # Stream event handler + group matching
├── VolMon.CLI/            # Command-line tool
│   └── Program.cs         # Argument parsing + IPC commands
└── VolMon.GUI/            # Avalonia tray app
    ├── Program.cs         # App builder
    ├── App.axaml(.cs)     # Application + tray icon setup
    ├── Views/             # AXAML windows
    └── ViewModels/        # MVVM view models
```

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

### Editing the config schema

1. Modify `VolMonConfig.cs` and/or `AudioGroup.cs` / `StreamMatcher.cs`
2. Existing config files will need migration (or just delete and recreate)
