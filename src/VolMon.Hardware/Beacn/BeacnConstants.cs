namespace VolMon.Hardware.Beacn;

/// <summary>
/// USB constants shared across all Beacn devices.
/// </summary>
internal static class BeacnConstants
{
    /// <summary>Beacn USB Vendor ID.</summary>
    public const int VendorId = 0x33AE;

    /// <summary>Beacn Mix USB Product ID.</summary>
    public const int MixProductId = 0x0004;

    /// <summary>Beacn Mix Create USB Product ID.</summary>
    public const int MixCreateProductId = 0x0007;

    /// <summary>USB interface to claim for controller communication.</summary>
    public const int InterfaceNumber = 0;

    /// <summary>USB alternate setting for the controller interface.</summary>
    public const int AltSetting = 1;

    /// <summary>USB write timeout in milliseconds.</summary>
    public const int WriteTimeoutMs = 2000;

    /// <summary>USB read timeout in milliseconds.</summary>
    public const int ReadTimeoutMs = 2000;

    /// <summary>Polling interval for input state (milliseconds).</summary>
    public const int PollIntervalMs = 50;

    /// <summary>Input buffer size for interrupt reads.</summary>
    public const int InputBufferSize = 64;

    // ── Command opcodes (byte 3) ────────────────────────────────────

    /// <summary>Get device info (firmware version + serial).</summary>
    public const byte CmdGetDeviceInfo = 0x01;

    /// <summary>Set a parameter value.</summary>
    public const byte CmdSetParam = 0x04;

    /// <summary>Poll for input state (firmware >= 1.2.0.81).</summary>
    public const byte CmdPollInput = 0x05;

    /// <summary>Image data chunk.</summary>
    public const byte CmdImageChunk = 0x50;

    /// <summary>Wake / keep-alive.</summary>
    public const byte CmdWake = 0xF1;

    // ── Button bit positions in the 16-bit button mask ──────────────

    public const int ButtonDial1 = 8;
    public const int ButtonDial2 = 9;
    public const int ButtonDial3 = 10;
    public const int ButtonDial4 = 11;

    // ── Button lighting IDs (byte 1 of set-color command) ───────────

    public const byte LightDial1 = 0;
    public const byte LightDial2 = 1;
    public const byte LightDial3 = 2;
    public const byte LightDial4 = 3;
    public const byte LightMix = 4;
    public const byte LightLeft = 5;
    public const byte LightRight = 6;

    /// <summary>Default display brightness (0-100).</summary>
    public const byte DefaultDisplayBrightness = 40;

    /// <summary>Default button LED brightness (0-10).</summary>
    public const byte DefaultButtonBrightness = 8;

    // ── Firmware version threshold for poll vs. notify mode ─────────

    /// <summary>
    /// Firmware versions above this use poll mode (send 0x05 then read).
    /// At or below this version, the device sends input data automatically.
    /// Encoded as: major.minor.patch.build where threshold is 1.2.0.80.
    /// </summary>
    public const uint PollModeMinVersion = 0x12000051; // 1.2.0.81
}
