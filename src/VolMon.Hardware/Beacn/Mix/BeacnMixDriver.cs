using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;

namespace VolMon.Hardware.Beacn.Mix;

/// <summary>
/// Device driver for Beacn Mix controllers.
/// Handles USB enumeration and controller instantiation.
/// </summary>
internal sealed class BeacnMixDriver : IDeviceDriver
{
    private const string DeviceIdPrefix = "beacn-mix-";

    public string DriverType => "beacn-mix";

    public IReadOnlyList<DetectedDevice> Scan(IReadOnlySet<string> activeDeviceIds)
    {
        var results = new List<DetectedDevice>();

        using var context = new UsbContext();
        using var deviceList = context.List();

        // Count how many USB devices match our VID/PID
        var matchingDevices = new List<UsbDevice>();
        foreach (var usbDevice in deviceList)
        {
            if (usbDevice.VendorId == BeacnConstants.VendorId &&
                usbDevice.ProductId == BeacnConstants.MixProductId &&
                usbDevice is UsbDevice device)
            {
                matchingDevices.Add(device);
            }
        }

        // Count how many active sessions belong to this driver
        var activeCount = activeDeviceIds.Count(id => id.StartsWith(DeviceIdPrefix, StringComparison.Ordinal));

        // If all matching USB devices are accounted for by active sessions,
        // report them as still present without opening/disturbing them.
        if (matchingDevices.Count <= activeCount && matchingDevices.Count > 0)
        {
            foreach (var id in activeDeviceIds)
            {
                if (id.StartsWith(DeviceIdPrefix, StringComparison.Ordinal))
                {
                    var serial = id[DeviceIdPrefix.Length..];
                    results.Add(new DetectedDevice
                    {
                        DeviceId = id,
                        DeviceName = "Beacn Mix",
                        DriverType = DriverType,
                        Serial = serial,
                        HasDisplay = true
                    });
                }
            }
            return results;
        }

        // There are new/unknown devices — we need to open unidentified ones to read serials.
        // Only open devices that we don't already have sessions for.
        foreach (var device in matchingDevices)
        {
            var serial = TryReadSerial(device);
            if (serial is null)
                continue;

            var deviceId = $"{DeviceIdPrefix}{serial}";

            results.Add(new DetectedDevice
            {
                DeviceId = deviceId,
                DeviceName = "Beacn Mix",
                DriverType = DriverType,
                Serial = serial,
                HasDisplay = true
            });
        }

        return results;
    }

    public IHardwareController CreateController(DetectedDevice device, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<BeacnMixController>();
        return new BeacnMixController(logger, device.Serial);
    }

    /// <summary>
    /// Briefly open a USB device to read its serial number via the Beacn device info command,
    /// then close it. Returns null if the serial couldn't be read (e.g. device already in use).
    /// </summary>
    private static string? TryReadSerial(UsbDevice device)
    {
        try
        {
            device.Open();

            try
            {
                device.ClaimInterface(BeacnConstants.InterfaceNumber);
                device.SetAltInterface(BeacnConstants.InterfaceNumber, BeacnConstants.AltSetting);

                var writer = device.OpenEndpointWriter(WriteEndpointID.Ep03, EndpointType.Interrupt);
                var reader = device.OpenEndpointReader(ReadEndpointID.Ep03, 64, EndpointType.Interrupt);

                // Clear halt
                device.ControlTransfer(new UsbSetupPacket(0x02, 0x01, 0x00, 0x83, 0x00));

                // Send get-device-info command
                var cmd = new byte[64];
                cmd[3] = BeacnConstants.CmdGetDeviceInfo;
                writer.Write(cmd, 0, cmd.Length, 1000, out _);

                // Read response
                var buffer = new byte[64];
                reader.Read(buffer, 0, buffer.Length, 1000, out var read);

                if (read > 8)
                {
                    // Serial is at bytes 8+ as null-terminated ASCII (alphanumeric only),
                    // matching the parsing in BeacnMixDevice.GetDeviceInfo
                    var serialChars = new List<char>();
                    for (var i = 8; i < read && buffer[i] != 0; i++)
                    {
                        var c = (char)buffer[i];
                        if (char.IsLetterOrDigit(c))
                            serialChars.Add(c);
                    }

                    if (serialChars.Count > 0)
                        return new string(serialChars.ToArray());
                }
            }
            finally
            {
                try { device.ReleaseInterface(BeacnConstants.InterfaceNumber); } catch { }
                try { device.Close(); } catch { }
            }
        }
        catch
        {
            // Device might be in use or not accessible
        }

        return null;
    }
}
