# VolMon.Hardware

The hardware daemon — a separate process that bridges physical USB controllers
(dials, buttons, displays) to the VolMon daemon via named pipe IPC.

## How It Works

1. Connects to the VolMon daemon over named pipe (`volmon-daemon`)
2. Scans USB bus periodically for supported devices
3. Starts a crash-isolated session for each enabled device
4. Dial rotation sends `set-group-volume` commands to the daemon
5. Button press sends `mute-group`/`unmute-group` commands
6. Daemon state changes (volume, mute, group list) are pushed back and
   reflected on device LEDs and displays

## Architecture

```
HardwareBridgeService (BackgroundService)
    │
    ├── IpcDuplexClient ──────▸ VolMon.Daemon (named pipe)
    │
    └── DeviceManager
            │
            ├── IDeviceDriver[]      (one per device family)
            │   └── Scan() ──▸ DetectedDevice[]
            │
            ├── hardware.json        (FileSystemWatcher)
            │
            └── DeviceSession[]      (one per enabled device)
                    │
                    ├── IHardwareController  (USB I/O, input, display, LEDs)
                    │
                    ├── Dial rotation ──▸ debounce(30ms) ──▸ set-group-volume
                    ├── Button press  ──▸ mute-group / unmute-group
                    └── State update  ◂── daemon push event (echo suppress 200ms)
```

### Component Responsibilities

| Component | Purpose |
|---|---|
| `HardwareBridgeService` | Top-level `BackgroundService`. Creates IPC client, DeviceManager, wires daemon state events to all sessions. |
| `DeviceManager` | Scan loop (configurable interval, default 5s). Reconciles detected USB devices against `hardware.json`. Starts/stops `DeviceSession` instances. Watches `hardware.json` for live enable/disable. |
| `DeviceSession` | Wraps one `IHardwareController` with IPC bridge logic. Converts hardware events to daemon commands. Applies 30ms dial debounce and 200ms echo suppression. Crash-isolated — runs in its own `Task`, marked `Faulted` on exception, auto-restarted next scan. |
| `IDeviceDriver` | Discovers USB devices of a specific type, reads serial numbers, creates controllers. Skips devices with active sessions to avoid disrupting them. |
| `IHardwareController` | Manages a single physical device: USB open/close, input polling, display rendering, LED colors. Raises `DialRotated` and `ButtonPressed` events. |

### Crash Isolation

Each device runs in its own `DeviceSession` background task. If a device throws
(USB disconnect, communication error, etc.):

1. The session catches the exception and sets `State = Faulted`
2. Other device sessions continue running unaffected
3. On the next scan cycle, `DeviceManager` detects the faulted session, disposes
   it, and creates a new one (if the device is still physically connected)

## Config

### Master config: `~/.config/volmon/hardware.json`

```json
{
  "devices": {
    "beacn-mix-0041220700598": {
      "name": "Beacn Mix",
      "driver": "beacn-mix",
      "serial": "0041220700598",
      "hasDisplay": true,
      "enabled": true
    }
  },
  "scanIntervalSeconds": 5
}
```

- New devices are added automatically with `enabled: false`
- The daemon does not interact with disabled devices at all
- `hasDisplay`, `name`, and `driver` are updated by the driver on each scan
  (driver-authoritative fields)
- Changes are picked up live via `FileSystemWatcher` — no daemon restart needed

### Per-device config: `~/.config/volmon/beacn-mix-{serial}.json`

```json
{
  "displayBrightness": 40,
  "dimBrightness": 1,
  "buttonBrightness": 8,
  "dimTimeoutSeconds": 30,
  "offTimeoutSeconds": 60,
  "volumeStepPerDelta": 1,
  "layout": "VolMon_Layout_BeacnMix_default-vertical"
}
```

- Created automatically with defaults when a device first connects
- Changes are picked up live via `FileSystemWatcher` (brightness applied
  immediately, layout reloaded on name change)

## Display System

Devices with `HasDisplay = true` (like the Beacn Mix) use a JSON-based template
system for screen rendering.

### Layout Files

Layout files are JSON files defining visual slots drawn in order:

- **Resolution order**: bundled `Layouts/` dir next to binary, then config dir
  (`~/.config/volmon/`), then hardcoded fallback
- **Security**: file names are sanitized (path separators stripped), resolved
  paths are verified to be inside allowed directories
- **Bundled presets** are prefixed with `VolMon_Layout_BeacnMix_`
- Config stores the full layout name (e.g. `"VolMon_Layout_BeacnMix_default-vertical"`)

### Slot Types

| Type | Purpose |
|---|---|
| `Rect` | Filled/outlined rectangle |
| `Text` | Text with font size, weight, alignment |
| `Bar` | Volume bar (vertical or horizontal) |
| `Arc` | Horseshoe/arc volume indicator |
| `Line` | Straight line |
| `Checkbox` | Boolean indicator (e.g. mute state) |
| `Image` | PNG/JPEG image |

### Data Bindings

Slots support `{group.*}` bindings that resolve against the current group state:

| Binding | Resolves to |
|---|---|
| `{group.name}` | Group display name |
| `{group.volume}` | Volume (0-100) |
| `{group.muted}` | Boolean mute state |
| `{group.color}` | Hex color string |

### Repeat

Slots with a `repeat` property (e.g. `"0-3"`) are duplicated for each group
index, with `repeatOffsetX`/`repeatOffsetY` shifting each instance. Child slots
inherit the repeat context.

### Rendering

`TemplateRenderer` renders the layout to a JPEG image using SkiaSharp. The JPEG
is sent to the device via USB interrupt transfers. Rendering is signal-based
(`ManualResetEventSlim`) — only triggered when state changes, not on a timer.

### Display Power Management

| State | Trigger | Action |
|---|---|---|
| Active | User input | Full brightness from config |
| Dimmed | No input for `dimTimeoutSeconds` (default 30s) | Reduced brightness (`dimBrightness`, default 1%) |
| Off | No input for `offTimeoutSeconds` (default 60s) | Display disabled |
| Wake | Any dial/button input | Re-enable display, restore brightness, re-render |

The display is also turned off on daemon shutdown.

## Adding a New Device

### 1. Create the directory structure

```
src/VolMon.Hardware/
└── YourBrand/
    └── YourDevice/
        ├── YourDeviceDriver.cs      # IDeviceDriver
        ├── YourDeviceDevice.cs      # Low-level USB I/O
        └── YourDeviceController.cs  # IHardwareController
```

### 2. Implement `IDeviceDriver`

```csharp
internal sealed class YourDeviceDriver : IDeviceDriver
{
    public string DriverType => "yourbrand-yourdevice";

    public IReadOnlyList<DetectedDevice> Scan(IReadOnlySet<string> activeDeviceIds)
    {
        // 1. Enumerate USB devices matching your VID/PID
        // 2. Skip devices whose IDs are in activeDeviceIds
        // 3. Briefly open new devices to read serial numbers
        // 4. Return DetectedDevice list
    }

    public IHardwareController CreateController(DetectedDevice device, ILoggerFactory loggerFactory)
    {
        // Create and return your controller instance
    }
}
```

Key rules:
- **Never open devices that have active sessions** (check `activeDeviceIds`)
- Set `HasDisplay = true` in `DetectedDevice` if the device has a screen
- Serial numbers are used as config file keys — they must be stable and unique

### 3. Implement `IHardwareController`

- `StartAsync` — open USB, initialize device, start input polling
- `StopAsync` — clean up, turn off display
- Raise `DialRotated` for dial/knob input
- Raise `ButtonPressed` for button input
- `SetDialColorAsync` — LED feedback (group color, red when muted)
- `UpdateDisplay` — render group state to screen (if applicable)

### 4. Add per-device config (if needed)

Add a config class to `VolMon.Hardware.Models`:

```csharp
public sealed class YourDeviceConfig
{
    // Device-specific settings
    public static string GetConfigPath(string serial) { ... }
    public static async Task<YourDeviceConfig> LoadAsync(string serial) { ... }
}
```

### 5. Register the driver

In `Program.cs`, add your driver to the driver list:

```csharp
var drivers = new IDeviceDriver[]
{
    new BeacnMixDriver(),
    new YourDeviceDriver(),  // Add here
};
```

### 6. Safety

- **Never upload firmware** — only use the device's existing USB protocol
- **Never write to USB endpoints** that you haven't verified are safe
- Handle USB disconnection gracefully (the session system will auto-restart)
