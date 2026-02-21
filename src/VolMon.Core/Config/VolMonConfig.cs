using VolMon.Core.Audio;

namespace VolMon.Core.Config;

/// <summary>
/// Root configuration model. Serialized to/from JSON.
/// </summary>
public sealed class VolMonConfig
{
    /// <summary>Configured audio groups.</summary>
    public List<AudioGroup> Groups { get; set; } = [];

    /// <summary>Global shortcut key bindings (GUI only).</summary>
    public ShortcutConfig Shortcuts { get; set; } = new();
}

/// <summary>
/// Configurable global shortcut key bindings.
/// Each value is a key combination string: optional modifiers (Ctrl, Alt, Shift, Meta)
/// joined with '+', followed by the key name (e.g. "F13", "Ctrl+F1", "Alt+Shift+Up").
/// </summary>
public sealed class ShortcutConfig
{
    /// <summary>Key to increase volume of the targeted group by 5.</summary>
    public string VolumeUp { get; set; } = "F13";

    /// <summary>Key to decrease volume of the targeted group by 5.</summary>
    public string VolumeDown { get; set; } = "F14";

    /// <summary>Key to cycle the target to the next group.</summary>
    public string SelectNextGroup { get; set; } = "F15";

    /// <summary>Key to cycle the target to the previous group.</summary>
    public string SelectPreviousGroup { get; set; } = "F16";

    /// <summary>Key to toggle mute on the targeted group.</summary>
    public string MuteToggle { get; set; } = "F17";
}
