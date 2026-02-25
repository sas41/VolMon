# VolMon.Hardware.Models

Shared configuration DTOs for the hardware daemon and hardware GUI.

## Why This Exists

`VolMon.Hardware` uses **SkiaSharp 3.119.0** for display rendering.
`VolMon.HardwareGUI` uses **Avalonia 11.3.12** which depends on **SkiaSharp
2.88.9**. These two SkiaSharp versions are ABI-incompatible — they cannot
coexist in the same process.

To allow both projects to share config models, this project contains only plain
DTOs with no SkiaSharp dependency. Both `VolMon.Hardware` and
`VolMon.HardwareGUI` reference this project.

**Rule: `VolMon.HardwareGUI` must never reference `VolMon.Hardware` directly.**

## Contents

### `HardwareConfig`

Master configuration stored at `~/.config/volmon/hardware.json`.

| Type | Purpose |
|---|---|
| `HardwareConfig` | Root config: `Dictionary<string, DeviceEntry>` + `ScanIntervalSeconds` |
| `DeviceEntry` | Per-device: `Name`, `Driver`, `Serial`, `HasDisplay`, `Enabled` |

`DeviceEntry.Enabled` defaults to `false` — new devices are disabled until
explicitly enabled by the user.

### `BeacnMixConfig`

Per-device configuration stored at `~/.config/volmon/beacn-mix-{serial}.json`.

Fields: `DisplayBrightness`, `DimBrightness`, `ButtonBrightness`,
`DimTimeoutSeconds`, `OffTimeoutSeconds`, `VolumeStepPerDelta`, `Layout`.

## Adding New Device Configs

When adding support for a new hardware device that has device-specific settings:

1. Create a new config class in this project (e.g. `YourDeviceConfig.cs`)
2. Follow the same pattern as `BeacnMixConfig` — static `LoadAsync`/`SaveAsync`,
   `GetConfigPath`, JSON serialization with camelCase
3. Do not add any SkiaSharp or other heavy dependencies to this project
