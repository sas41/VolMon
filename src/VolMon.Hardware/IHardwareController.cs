namespace VolMon.Hardware;

/// <summary>
/// Event args for a dial/knob rotation on a hardware controller.
/// </summary>
public sealed class DialRotatedEventArgs : EventArgs
{
    /// <summary>Zero-based dial index.</summary>
    public required int DialIndex { get; init; }

    /// <summary>Signed rotation delta (positive = clockwise/up, negative = counter-clockwise/down).</summary>
    public required int Delta { get; init; }
}

/// <summary>
/// Event args for a button press/release on a hardware controller.
/// </summary>
public sealed class ButtonPressedEventArgs : EventArgs
{
    /// <summary>Zero-based button index (for dial buttons, matches dial index).</summary>
    public required int ButtonIndex { get; init; }

    /// <summary>True if button was pressed down, false if released.</summary>
    public required bool Pressed { get; init; }
}

/// <summary>
/// Represents the state of a single group/dial for display rendering.
/// Generic across all device types that have displays.
/// </summary>
public readonly struct GroupDisplayState
{
    public string Name { get; init; }
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public string? Color { get; init; }

    /// <summary>
    /// Names of programs/devices currently producing audio in this group.
    /// Empty array if none are active. Never null.
    /// </summary>
    public string[] ActiveMembers { get; init; }

    /// <summary>
    /// Names of programs/devices configured in this group but not currently producing audio.
    /// Empty array if all members are active or none configured. Never null.
    /// </summary>
    public string[] InactiveMembers { get; init; }
}

/// <summary>
/// Abstraction for a physical hardware controller that can control audio groups.
/// Each controller runs as a background service and raises events when the user
/// interacts with physical controls (dials, buttons, etc.).
/// </summary>
public interface IHardwareController : IAsyncDisposable
{
    /// <summary>Raised when a dial/knob is rotated.</summary>
    event EventHandler<DialRotatedEventArgs>? DialRotated;

    /// <summary>Raised when a button is pressed or released.</summary>
    event EventHandler<ButtonPressedEventArgs>? ButtonPressed;

    /// <summary>
    /// Unique device identifier (e.g. "beacn-mix-0041220700598").
    /// Used as the key in hardware.json and for per-device config files.
    /// </summary>
    string DeviceId { get; }

    /// <summary>Human-readable name of this controller (e.g. "Beacn Mix").</summary>
    string DeviceName { get; }

    /// <summary>Number of dials/knobs available on this controller.</summary>
    int DialCount { get; }

    /// <summary>Whether the controller is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Volume change per unit of dial rotation (percentage points).</summary>
    int VolumeStepPerDelta { get; }

    /// <summary>Whether knob acceleration is enabled (faster rotation = larger steps).</summary>
    bool KnobAcceleration { get; }

    /// <summary>Minimum delta magnitude before acceleration kicks in.</summary>
    int AccelerationThreshold { get; }

    /// <summary>Maximum multiplier applied during fast rotation.</summary>
    int AccelerationMaxMultiplier { get; }

    /// <summary>Delta magnitude at which acceleration reaches its maximum.</summary>
    int AccelerationSaturation { get; }

    /// <summary>Whether this device has a display that can show group state.</summary>
    bool HasDisplay { get; }

    /// <summary>
    /// Start the controller. Opens the device, begins polling for input.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Stop the controller. Closes the device connection.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Set the LED color for a dial button (for mute/unmute feedback).
    /// Color is RGBA (0-255 per channel).
    /// </summary>
    Task SetDialColorAsync(int dialIndex, byte r, byte g, byte b, byte a = 255);

    /// <summary>
    /// Update the display with the current group state.
    /// No-op for devices without a display.
    /// </summary>
    void UpdateDisplay(GroupDisplayState[] groups);
}
