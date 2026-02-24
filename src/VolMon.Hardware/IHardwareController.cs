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

    /// <summary>Human-readable name of this controller (e.g. "Beacn Mix").</summary>
    string DeviceName { get; }

    /// <summary>Number of dials/knobs available on this controller.</summary>
    int DialCount { get; }

    /// <summary>Whether the controller is currently connected.</summary>
    bool IsConnected { get; }

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
}
