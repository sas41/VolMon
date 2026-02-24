using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace VolMon.Hardware.Beacn.Mix;

/// <summary>
/// Low-level USB communication with a Beacn Mix device.
/// Handles device discovery, connection, initialization, input polling,
/// and LED control. Does NOT handle audio — that's the daemon's job.
/// </summary>
internal sealed class BeacnMixDevice : IDisposable
{
    private UsbContext? _context;
    private UsbDeviceCollection? _deviceList;
    private UsbDevice? _device;
    private UsbEndpointWriter? _writer;
    private UsbEndpointReader? _reader;
    private bool _disposed;

    /// <summary>Firmware version of the connected device.</summary>
    public BeacnVersion FirmwareVersion { get; private set; }

    /// <summary>Serial number of the connected device.</summary>
    public string SerialNumber { get; private set; } = string.Empty;

    /// <summary>Whether the device is currently open and connected.</summary>
    public bool IsOpen => _device is not null && !_disposed;

    /// <summary>Whether to use poll mode (true) or notify mode (false) for input.</summary>
    public bool UsePollMode => FirmwareVersion >= new BeacnVersion(BeacnConstants.PollModeMinVersion);

    /// <summary>
    /// Attempt to find and open a Beacn Mix device.
    /// Returns true if a device was found and opened successfully.
    /// </summary>
    public bool TryOpen()
    {
        if (IsOpen) return true;

        _context = new UsbContext();

        // Keep the device list alive — disposing it invalidates device handles
        _deviceList = _context.List();
        var usbDevice = _deviceList.FirstOrDefault(d =>
            d.VendorId == BeacnConstants.VendorId &&
            d.ProductId == BeacnConstants.MixProductId) as UsbDevice;

        if (usbDevice is null)
        {
            _deviceList.Dispose();
            _deviceList = null;
            _context.Dispose();
            _context = null;
            return false;
        }

        try
        {
            usbDevice.Open();
            _device = usbDevice;

            // Claim the controller interface
            _device.ClaimInterface(BeacnConstants.InterfaceNumber);
            _device.SetAltInterface(BeacnConstants.InterfaceNumber, BeacnConstants.AltSetting);

            // Open endpoints — Beacn Mix uses EP 0x03 (write) and EP 0x83 (read)
            // Must specify EndpointType.Interrupt since the device uses interrupt transfers
            _writer = _device.OpenEndpointWriter(WriteEndpointID.Ep03, EndpointType.Interrupt);
            _reader = _device.OpenEndpointReader(ReadEndpointID.Ep03, 64, EndpointType.Interrupt);

            // Clear halt on the read endpoint via control transfer
            // CLEAR_FEATURE(ENDPOINT_HALT) for EP 0x83
            _device.ControlTransfer(
                new UsbSetupPacket(0x02, 0x01, 0x00, 0x83, 0x00));

            // Get device info (firmware version + serial)
            GetDeviceInfo();

            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    /// <summary>
    /// Initialize the device after connection: enable screen, set brightness, wake.
    /// </summary>
    public void Initialize(BeacnMixConfig config)
    {
        EnsureOpen();

        // Enable screen
        SetDisplayEnabled(true);

        // Set display brightness from config
        SetDisplayBrightness(config.DisplayBrightness);

        // Set button brightness from config
        SetButtonBrightness(config.ButtonBrightness);

        // Wake device
        Wake();
    }

    /// <summary>
    /// Enable or disable the display.
    /// </summary>
    public void SetDisplayEnabled(bool enabled)
    {
        EnsureOpen();
        byte val = enabled ? (byte)0x00 : (byte)0x01;
        WriteCommand(0x00, 0x01, 0x00, BeacnConstants.CmdSetParam, val, 0x00, 0x00, 0x00);
    }

    /// <summary>
    /// Set display brightness (0-100).
    /// </summary>
    public void SetDisplayBrightness(int brightness)
    {
        EnsureOpen();
        var val = (byte)Math.Clamp(brightness, 0, 100);
        WriteCommand(0x00, 0x00, 0x00, BeacnConstants.CmdSetParam, val, 0x00, 0x00, 0x00);
    }

    /// <summary>
    /// Set button LED brightness (0-10).
    /// </summary>
    public void SetButtonBrightness(int brightness)
    {
        EnsureOpen();
        var val = (byte)Math.Clamp(brightness, 0, 10);
        WriteCommand(0x01, 0x07, 0x00, BeacnConstants.CmdSetParam, val, 0x00, 0x00, 0x00);
    }

    // Reusable chunk buffer (4-byte header + up to 1020 bytes of data)
    private readonly byte[] _chunkBuffer = new byte[1024];
    private readonly byte[] _completeBuffer = new byte[16];

    /// <summary>
    /// Send a JPEG image to the display at position (0,0).
    /// The image data is sent in 1020-byte chunks over interrupt transfers.
    /// </summary>
    public void SendImage(byte[] jpegData)
    {
        EnsureOpen();

        const int chunkDataSize = 1020; // 1024 - 4 byte header
        var chunkCount = (jpegData.Length + chunkDataSize - 1) / chunkDataSize;

        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkDataSize;
            var remaining = Math.Min(chunkDataSize, jpegData.Length - offset);
            var packetLen = 4 + remaining;

            // Chunk index as little-endian u24 in bytes 0-2
            _chunkBuffer[0] = (byte)(i & 0xFF);
            _chunkBuffer[1] = (byte)((i >> 8) & 0xFF);
            _chunkBuffer[2] = (byte)((i >> 16) & 0xFF);
            _chunkBuffer[3] = BeacnConstants.CmdImageChunk;

            Array.Copy(jpegData, offset, _chunkBuffer, 4, remaining);

            WriteCommandWithRetry(_chunkBuffer, packetLen);
        }

        // Send the "complete" packet
        Array.Clear(_completeBuffer);
        _completeBuffer[0] = 0xFF;
        _completeBuffer[1] = 0xFF;
        _completeBuffer[2] = 0xFF;
        _completeBuffer[3] = BeacnConstants.CmdImageChunk;

        // Total size - 1 as u32 LE
        var sizeMinusOne = (uint)(jpegData.Length - 1);
        BitConverter.TryWriteBytes(_completeBuffer.AsSpan(4, 4), sizeMinusOne);

        // X position = 0, Y position = 0 (both u32 LE, already zero)

        WriteCommandWithRetry(_completeBuffer, 16);
    }

    /// <summary>
    /// Poll for input state. Sends the poll command (if needed) and reads the response.
    /// Returns the raw 64-byte input buffer, or null if the read timed out.
    /// </summary>
    public byte[]? PollInput()
    {
        EnsureOpen();

        if (UsePollMode)
        {
            // Send poll request
            WriteCommand(0x00, 0x00, 0x00, BeacnConstants.CmdPollInput);
        }

        var buffer = new byte[BeacnConstants.InputBufferSize];
        var ec = _reader!.Read(buffer, UsePollMode ? BeacnConstants.ReadTimeoutMs : 60, out var bytesRead);

        if (ec == Error.Success && bytesRead >= 10)
            return buffer;

        // Timeout is expected in notify mode when no input occurs
        if (ec == Error.Timeout || ec == Error.Overflow)
            return null;

        // Any other error — device may have disconnected
        if (ec != Error.Success)
            throw new IOException($"USB read error: {ec}");

        return null;
    }

    /// <summary>
    /// Set the LED color of a dial button.
    /// </summary>
    /// <param name="lightId">Button lighting ID (0-6, see BeacnConstants.Light*).</param>
    /// <param name="r">Red (0-255).</param>
    /// <param name="g">Green (0-255).</param>
    /// <param name="b">Blue (0-255).</param>
    /// <param name="a">Alpha (0-255).</param>
    public void SetButtonColor(byte lightId, byte r, byte g, byte b, byte a = 255)
    {
        EnsureOpen();
        // Color format is BGRA
        WriteCommand(0x01, lightId, 0x00, BeacnConstants.CmdSetParam, b, g, r, a);
    }

    /// <summary>
    /// Send the wake/keep-alive command to prevent the display from sleeping.
    /// </summary>
    public void Wake()
    {
        EnsureOpen();
        WriteCommand(0x00, 0x00, 0x00, BeacnConstants.CmdWake);
    }

    /// <summary>
    /// Close the device connection and release all resources.
    /// </summary>
    public void Close()
    {
        _writer = null;
        _reader = null;

        if (_device is not null)
        {
            try { _device.ReleaseInterface(BeacnConstants.InterfaceNumber); } catch { }
            try { _device.Close(); } catch { }
            _device = null;
        }

        if (_deviceList is not null)
        {
            try { _deviceList.Dispose(); } catch { }
            _deviceList = null;
        }

        if (_context is not null)
        {
            try { _context.Dispose(); } catch { }
            _context = null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Close();
        }
    }

    // ── Private helpers ─────────────────────────────────────────────

    private void GetDeviceInfo()
    {
        WriteCommand(0x00, 0x00, 0x00, BeacnConstants.CmdGetDeviceInfo);

        var buffer = new byte[BeacnConstants.InputBufferSize];
        var ec = _reader!.Read(buffer, BeacnConstants.ReadTimeoutMs, out var bytesRead);
        if (ec != Error.Success || bytesRead < 8)
            throw new IOException($"Failed to read device info: {ec}");

        // Bytes 4-7: firmware version (u32 LE)
        FirmwareVersion = BeacnVersion.FromBytes(buffer.AsSpan(4, 4));

        // Bytes 8+: null-terminated ASCII serial number (alphanumeric only)
        var serialChars = new List<char>();
        for (var i = 8; i < bytesRead && buffer[i] != 0; i++)
        {
            var c = (char)buffer[i];
            if (char.IsLetterOrDigit(c))
                serialChars.Add(c);
        }
        SerialNumber = new string(serialChars.ToArray());
    }

    private void WriteCommand(params byte[] data)
    {
        if (_writer is null)
            throw new InvalidOperationException("Device not connected.");

        var ec = _writer.Write(data, BeacnConstants.WriteTimeoutMs, out _);
        if (ec != Error.Success)
            throw new IOException($"USB write error: {ec}");
    }

    private void WriteCommandWithRetry(byte[] data, int length, int maxRetries = 100)
    {
        if (_writer is null)
            throw new InvalidOperationException("Device not connected.");

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var ec = _writer.Write(data, 0, length, 100, out _);
            if (ec == Error.Success)
                return;
            if (ec != Error.Timeout)
                throw new IOException($"USB write error: {ec}");
        }

        throw new IOException("USB write failed after max retries");
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
            throw new InvalidOperationException("Device is not open.");
    }
}
