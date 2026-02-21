using VolMon.Core.Audio;

namespace VolMon.Core.Config;

/// <summary>
/// Root configuration model. Serialized to/from JSON.
/// </summary>
public sealed class VolMonConfig
{
    /// <summary>Configured audio groups.</summary>
    public List<AudioGroup> Groups { get; set; } = [];
}
