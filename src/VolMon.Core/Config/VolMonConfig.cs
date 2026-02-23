using VolMon.Core.Audio;

namespace VolMon.Core.Config;

/// <summary>
/// Root configuration model. Serialized to/from JSON.
/// </summary>
public sealed class VolMonConfig
{
    /// <summary>Configured audio groups.</summary>
    public List<AudioGroup> Groups { get; set; } = [];

    /// <summary>
    /// Programs explicitly ignored by the user. These are never auto-assigned
    /// to a group and their volume is never changed. Not counted as a group.
    /// </summary>
    public List<string> IgnoredPrograms { get; set; } = [];

    /// <summary>Global shortcut key bindings (GUI only).</summary>
    public ShortcutConfig Shortcuts { get; set; } = new();

    /// <summary>
    /// The group last targeted by the shortcut system. Persisted so the
    /// next session resumes on the same group.
    /// </summary>
    public Guid? LastTargetGroupId { get; set; }
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
