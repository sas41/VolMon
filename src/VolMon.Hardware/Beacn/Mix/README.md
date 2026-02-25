# Beacn Mix USB Protocol

Reverse-engineered USB protocol documentation for the Beacn Mix and Beacn Mix
Create controllers. This was derived from analyzing the
[beacn-lib](../../example/beacn-lib-main/) Rust library and USB traffic
captures, then validated against real hardware (Beacn Mix, FW 1.2.0.83).

**Safety note**: VolMon only reads input and sends display/LED/brightness
commands. It never uploads firmware or modifies device configuration stored
on the device itself.

## Device Identification

| Field | Beacn Mix | Beacn Mix Create |
|---|---|---|
| USB VID | `0x33AE` | `0x33AE` |
| USB PID | `0x0004` | `0x0007` |
| Interface | 0 | 0 |
| Alt Setting | 1 | 1 |
| Write Endpoint | EP 0x03 (Interrupt OUT) | EP 0x03 (Interrupt OUT) |
| Read Endpoint | EP 0x83 (Interrupt IN) | EP 0x83 (Interrupt IN) |
| Display | 800x480 LCD | 800x480 LCD |
| Dials | 4 rotary encoders with push buttons | 4 rotary encoders with push buttons |

## Connection Sequence

1. Open the USB device
2. Claim interface 0
3. Set alt setting 1
4. Open interrupt write endpoint (EP 0x03) and read endpoint (EP 0x83)
5. Clear halt on read endpoint via control transfer:
   `CLEAR_FEATURE(ENDPOINT_HALT)` for EP 0x83
   (`bmRequestType=0x02, bRequest=0x01, wValue=0x00, wIndex=0x83, wLength=0x00`)
6. Send `GetDeviceInfo` command to read firmware version and serial number
7. Initialize: enable display, set brightness, send wake command

## Packet Format

All commands are sent as variable-length interrupt transfers on EP 0x03. The
first 4 bytes form the command header:

```
Byte 0: Parameter byte 0 (varies by command)
Byte 1: Parameter byte 1 (varies by command)
Byte 2: Parameter byte 2 (always 0x00 for non-image commands)
Byte 3: Opcode
Byte 4+: Payload (varies by command)
```

The opcode in byte 3 determines the command type.

## Command Reference

### GetDeviceInfo (opcode `0x01`)

Request firmware version and serial number.

**Send:**
```
00 00 00 01
```

**Response** (64 bytes, interrupt read):
```
Byte  0-3: Echo / header
Byte  4-7: Firmware version (u32 little-endian, packed)
Byte 8-N:  Serial number (null-terminated ASCII, alphanumeric only)
```

### Firmware Version Encoding

The firmware version is a 32-bit value with packed nibble/byte fields:

```
Bits 31-28: Major  (4 bits)
Bits 27-24: Minor  (4 bits)
Bits 23-16: Patch  (8 bits)
Bits 15-0:  Build  (16 bits)
```

Example: firmware 1.2.0.83 = `0x12000053`
- Major = 1 (`0x1`)
- Minor = 2 (`0x2`)
- Patch = 0 (`0x00`)
- Build = 83 (`0x0053`)

The raw 4 bytes are read as a **little-endian** u32 from the USB buffer.

### Serial Number

Starting at byte 8 of the `GetDeviceInfo` response, the serial number is a
null-terminated ASCII string containing only alphanumeric characters. Non-
alphanumeric bytes are filtered out during parsing.

The serial is variable length (e.g. `0041220700598` = 13 characters). It is
used as the unique device identifier and config file key.

### SetParam (opcode `0x04`)

Set a device parameter. The specific parameter is identified by bytes 0-1.

**General format:**
```
Byte 0: Param category
Byte 1: Param ID
Byte 2: 0x00
Byte 3: 0x04 (SetParam opcode)
Byte 4: Value byte 0
Byte 5: Value byte 1
Byte 6: Value byte 2
Byte 7: Value byte 3
```

#### Display Brightness

Set LCD backlight brightness (0-100):
```
00 00 00 04 [brightness] 00 00 00
```

#### Display Enable/Disable

Turn display on or off:
```
00 01 00 04 [0x00=on, 0x01=off] 00 00 00
```

#### Button LED Brightness

Set button LED brightness (0-10):
```
01 07 00 04 [brightness] 00 00 00
```

#### Button LED Color

Set the color of a specific LED:
```
01 [light_id] 00 04 [blue] [green] [red] [alpha]
```

Note: color format is **BGRA**, not RGBA.

Light IDs:

| ID | Location |
|---|---|
| 0 | Dial 1 (leftmost) |
| 1 | Dial 2 |
| 2 | Dial 3 |
| 3 | Dial 4 (rightmost) |
| 4 | Mix button |
| 5 | Left arrow |
| 6 | Right arrow |

### PollInput (opcode `0x05`)

Poll for input state (firmware >= 1.2.0.81). On older firmware, the device
sends input data automatically without polling.

**Send:**
```
00 00 00 05
```

**Response** (64 bytes, interrupt read):
```
Byte  0-3: Header
Byte  4:   Dial 1 delta (signed byte, positive = clockwise)
Byte  5:   Dial 2 delta
Byte  6:   Dial 3 delta
Byte  7:   Dial 4 delta
Byte  8-9: Button bitmask (big-endian u16)
Byte 10+:  Reserved / unused
```

#### Input Mode

The firmware version determines the input mode:

| Mode | Firmware | Behavior |
|---|---|---|
| **Poll** | >= 1.2.0.81 (`0x12000051`) | Send `0x05` command, then read response |
| **Notify** | < 1.2.0.81 | Device sends input data automatically; just read |

In poll mode, the read timeout is 2000ms (waiting for the response to the
poll command). In notify mode, the read timeout is 60ms (short, since data
arrives only when the user interacts).

#### Dial Deltas

Each dial delta is a **signed byte** (`sbyte`):
- Positive values = clockwise rotation
- Negative values = counter-clockwise rotation
- Zero = no movement
- Magnitude indicates rotation speed/amount within the poll interval

#### Button Bitmask

The button state is a 16-bit value (big-endian) in bytes 8-9:

```
buttons = (buffer[8] << 8) | buffer[9]
```

| Bit | Button |
|---|---|
| 8 | Dial 1 press |
| 9 | Dial 2 press |
| 10 | Dial 3 press |
| 11 | Dial 4 press |

A bit value of 1 = pressed, 0 = released. Button changes are detected by
XOR-ing the current bitmask with the previous one.

### Wake (opcode `0xF1`)

Send a wake/keep-alive command to prevent the device from entering its own
sleep mode:

```
00 00 00 F1
```

Send periodically (every few seconds) while the display is active.

### Image Transfer (opcode `0x50`)

Send a JPEG image to the device display. The image is split into chunks
sent as interrupt transfers.

#### Chunk Packets

Each chunk is up to 1024 bytes: 4-byte header + up to 1020 bytes of JPEG data.

```
Byte  0-2: Chunk index (24-bit little-endian unsigned integer)
Byte  3:   0x50 (ImageChunk opcode)
Byte  4+:  JPEG data (up to 1020 bytes)
```

The chunk index starts at 0 and increments for each chunk.

#### Completion Packet

After all chunks are sent, a 16-byte completion packet signals the end:

```
Byte  0-2: 0xFF 0xFF 0xFF (sentinel chunk index)
Byte  3:   0x50 (ImageChunk opcode)
Byte  4-7: Total JPEG size minus 1 (u32 little-endian)
Byte  8-11: X position (u32 LE, always 0)
Byte 12-15: Y position (u32 LE, always 0)
```

#### Transfer Details

- Images must be JPEG format at 800x480 resolution
- Lower JPEG quality = smaller file = fewer chunks = faster transfer
- VolMon uses quality 50 by default (configurable per-layout)
- Chunk writes use a retry loop (up to 100 attempts) with 100ms timeout per
  attempt, since the device may briefly NAK during display refresh
- The display position is always (0,0) — full-screen replacement

## USB Library Notes

VolMon uses [LibUsbDotNet 3.x](https://github.com/LibUsbDotNet/LibUsbDotNet)
for USB communication.

### Key API Details

- `UsbContext.List()` returns a `UsbDeviceCollection` — **keep it alive** as
  long as you're using any devices from it. Disposing the collection
  invalidates device handles.
- `SetAltInterface` is on the concrete `UsbDevice` class, not the interface.
- Endpoints must specify `EndpointType.Interrupt` explicitly (the device uses
  interrupt transfers, not bulk).
- `Error.Timeout` on reads is expected and not an error condition (e.g. in
  notify mode when no input has occurred).
- `Error.Overflow` can occur on reads and should be treated like a timeout.

### Permissions

On Linux, non-root USB access requires udev rules:

```
# /etc/udev/rules.d/99-volmon.rules
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0004", MODE="0666"
SUBSYSTEM=="usb", ATTR{idVendor}=="33ae", ATTR{idProduct}=="0007", MODE="0666"
```

Reload with: `sudo udevadm control --reload-rules && sudo udevadm trigger`

## Scan Safety

Periodic USB scanning must not disrupt active device connections. The
`BeacnMixDriver.Scan()` method receives a set of active session IDs. If all
USB devices matching the VID/PID are accounted for by active sessions, the
driver reports them as present without opening or disturbing them.

Only unidentified devices (new connections) are briefly opened to read their
serial number, then immediately closed. The `BeacnMixController` (via
`BeacnMixDevice`) manages the long-lived connection for actual communication.

## Debounce and Echo Suppression

These are handled in `DeviceSession`, not the USB layer:

- **Dial debounce** (30ms): rapid encoder ticks within a 30ms window are
  accumulated into a single volume command. Each new tick resets the timer.
- **Echo suppression** (200ms): after sending a volume/mute command, incoming
  daemon state updates for that dial are ignored for 200ms. This prevents the
  daemon's response from overwriting the local state during rapid interaction.
