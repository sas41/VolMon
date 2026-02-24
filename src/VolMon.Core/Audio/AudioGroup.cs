namespace VolMon.Core.Audio;

/// <summary>
/// Controls how VolMon applies volume to streams that belong to a group.
/// </summary>
public enum GroupMode
{
    /// <summary>
    /// Default. VolMon sets the PulseAudio sink-input volume directly.
    /// Simple and low-overhead, but the application's own volume slider
    /// (e.g. a browser's in-video slider) operates on the same knob and
    /// can temporarily override the group setting until the next
    /// StreamChanged correction.
    /// </summary>
    Direct,

    /// <summary>
    /// VolMon creates a per-group null-sink (virtual device) and redirects
    /// every stream in the group to it, then controls the null-sink's
    /// hardware volume. The application's stream volume is locked at 100%
    /// inside the virtual device, so the app's own slider has no effect on
    /// the audible output. Adds one resampling hop; not suitable for
    /// exclusive-mode or passthrough (e.g. HDMI bitstream, Bluetooth A2DP
    /// in hardware-volume mode).
    /// </summary>
    Compatibility,
}

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
    /// Display color for this group in the GUI, stored as a hex string (e.g. "#FF9500").
    /// Null means the GUI should auto-assign from its palette.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// When true, global shortcut cycling (next/previous group) skips this group.
    /// Volume/mute shortcuts also skip it while it is the target.
    /// </summary>
    public bool SkipShortcut { get; set; }

    /// <summary>
    /// Volume control mode for this group.
    /// <see cref="GroupMode.Direct"/> (default) sets sink-input volume directly.
    /// <see cref="GroupMode.Compatibility"/> routes streams through a per-group
    /// null-sink so that the application's own volume slider cannot override VolMon.
    /// </summary>
    public GroupMode Mode { get; set; } = GroupMode.Direct;

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
    /// Tests whether the given stream is in this group's program list.
    /// </summary>
    public bool ContainsProgram(AudioStream stream) =>
        Programs.Any(p => p.Equals(stream.BinaryName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tests whether the given stream binary name is in this group's program list.
    /// </summary>
    public bool ContainsProgram(string streamBinaryName) =>
        Programs.Any(p => p.Equals(streamBinaryName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tests whether the given device is in this group's device list.
    /// </summary>
    public bool ContainsDevice(AudioDevice device) =>

        Devices.Any(d => d.Equals(device.Name, StringComparison.OrdinalIgnoreCase));
    /// <summary>
    /// Tests whether the given device name is in this group's device list.
    /// </summary>
    public bool ContainsDevice(string deviceName) =>
        Devices.Any(d => d.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
}
