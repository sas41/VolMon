using System.Collections.Generic;

namespace VolMon.Core.Audio;

// Represents a running OS process and its associated audio streams
public sealed class AudioProcess
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Streams belonging to this process
    public List<AudioStream> Streams { get; set; } = new List<AudioStream>();
}
