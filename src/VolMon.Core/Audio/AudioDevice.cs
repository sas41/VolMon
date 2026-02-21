using System.Text.Json.Serialization;

namespace VolMon.Core.Audio;

/// <summary>
/// Represents a hardware audio device (speaker/headphone output or microphone input).
/// </summary>
public sealed class AudioDevice
{
    /// <summary>Backend-specific device identifier (e.g. PulseAudio sink/source index).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Stable device name (e.g. "alsa_output.pci-0000_00_1f.3.analog-stereo").
    /// This is what gets stored in config, not the numeric index.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description (e.g. "Built-in Audio Analog Stereo").</summary>
    public string? Description { get; init; }

    /// <summary>Whether this is an output (sink) or input (source) device.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DeviceType>))]
    public required DeviceType Type { get; init; }

    /// <summary>Current volume as a percentage 0-100.</summary>
    public int Volume { get; set; }

    /// <summary>Whether the device is currently muted.</summary>
    public bool Muted { get; set; }

    /// <summary>GUID of the group this device has been assigned to, if any.</summary>
    public Guid? AssignedGroup { get; set; }

    public override string ToString() =>
        $"[{Type}] {Name} ({Description ?? "?"}) vol={Volume}% muted={Muted}";
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceType>))]
public enum DeviceType
{
    /// <summary>Output device (speakers, headphones). PulseAudio "sink".</summary>
    Sink,

    /// <summary>Input device (microphone). PulseAudio "source".</summary>
    Source
}
