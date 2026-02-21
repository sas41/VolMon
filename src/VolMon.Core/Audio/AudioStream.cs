namespace VolMon.Core.Audio;

/// <summary>
/// Represents a single application audio stream (e.g. one PulseAudio sink-input).
/// </summary>
public sealed class AudioStream
{
    /// <summary>Backend-specific stream identifier (e.g. PulseAudio sink-input index).</summary>
    public required string Id { get; init; }

    /// <summary>Process binary name (e.g. "spotify", "firefox").</summary>
    public required string BinaryName { get; init; }

    /// <summary>
    /// Application class reported by the audio server (e.g. PulseAudio "application.name").
    /// May be null if the backend doesn't support it.
    /// </summary>
    public string? ApplicationClass { get; init; }

    /// <summary>Current volume as a percentage 0-100.</summary>
    public int Volume { get; set; }

    /// <summary>Whether the stream is currently muted.</summary>
    public bool Muted { get; set; }

    /// <summary>OS process ID, if available.</summary>
    public int? ProcessId { get; init; }

    /// <summary>GUID of the group this stream has been assigned to, if any.</summary>
    public Guid? AssignedGroup { get; set; }

    public override string ToString() =>
        $"[{Id}] {BinaryName} (class={ApplicationClass ?? "?"}) vol={Volume}% muted={Muted}";
}
