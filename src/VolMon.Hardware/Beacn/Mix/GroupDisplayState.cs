namespace VolMon.Hardware.Beacn.Mix;

/// <summary>
/// Represents the state of a single group column for rendering on a hardware display.
/// </summary>
internal readonly struct GroupDisplayState
{
    public string Name { get; init; }
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public string? Color { get; init; }
}
