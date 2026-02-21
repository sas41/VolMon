namespace VolMon.Core.Audio;

/// <summary>
/// A named group of audio streams and/or devices with shared volume and mute state.
/// Groups contain explicit program names and device names — no glob patterns.
/// </summary>
public sealed class AudioGroup
{
    /// <summary>
    /// Unique identifier for this group. Auto-assigned if not present in config.
    /// All internal references (stream/device assignment, IPC) use this GUID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Display name for this group.</summary>
    public required string Name { get; set; }

    /// <summary>Target volume for all members of this group (0-100).</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Whether all members of this group should be muted.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// If true, newly detected programs that aren't in any other group
    /// are automatically assigned here. Only one group should be default.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// If true, this is the special "Ignored" group. Programs in this group
    /// have their volume never changed by the daemon. There is exactly one
    /// ignored group and it cannot be deleted.
    /// </summary>
    public bool IsIgnored { get; set; }

    /// <summary>
    /// Display color for this group in the GUI, stored as a hex string (e.g. "#FF9500").
    /// Null means the GUI should auto-assign from its palette.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Process binary names that belong to this group (e.g. "spotify", "firefox").
    /// Matching is case-insensitive.
    /// </summary>
    public List<string> Programs { get; set; } = [];

    /// <summary>
    /// Audio device names that belong to this group.
    /// These are backend-specific identifiers (e.g. PulseAudio sink/source names
    /// like "alsa_output.pci-0000_00_1f.3.analog-stereo").
    /// </summary>
    public List<string> Devices { get; set; } = [];

    /// <summary>
    /// Tests whether the given stream's binary name is in this group's program list.
    /// </summary>
    public bool ContainsProgram(AudioStream stream) =>
        Programs.Any(p => p.Equals(stream.BinaryName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tests whether the given device name is in this group's device list.
    /// </summary>
    public bool ContainsDevice(string deviceName) =>
        Devices.Any(d => d.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
}
