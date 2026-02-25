using Microsoft.Extensions.Logging;

namespace VolMon.Hardware;

/// <summary>
/// Describes a detected hardware device before it is opened.
/// </summary>
public sealed class DetectedDevice
{
    /// <summary>Unique device ID (e.g. "beacn-mix-0041220700598").</summary>
    public required string DeviceId { get; init; }

    /// <summary>Human-readable name (e.g. "Beacn Mix").</summary>
    public required string DeviceName { get; init; }

    /// <summary>Driver type identifier (e.g. "beacn-mix").</summary>
    public required string DriverType { get; init; }

    /// <summary>Device serial number.</summary>
    public required string Serial { get; init; }

    /// <summary>Whether this device has a configurable display (supports layouts, brightness, etc.).</summary>
    public bool HasDisplay { get; init; }
}

/// <summary>
/// A device driver that can discover and create controllers for a specific
/// type of hardware device. Each supported device family has one driver.
/// </summary>
public interface IDeviceDriver
{
    /// <summary>Driver type identifier (e.g. "beacn-mix").</summary>
    string DriverType { get; }

    /// <summary>
    /// Scan for connected devices of this type.
    /// Devices whose IDs are in <paramref name="activeDeviceIds"/> must be skipped
    /// (they are already open by a running session and must not be disturbed).
    /// </summary>
    IReadOnlyList<DetectedDevice> Scan(IReadOnlySet<string> activeDeviceIds);

    /// <summary>
    /// Create a controller instance for a specific detected device.
    /// The controller is not started yet — call StartAsync on it.
    /// </summary>
    IHardwareController CreateController(DetectedDevice device, ILoggerFactory loggerFactory);
}
