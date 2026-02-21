using SharpHook;
using SharpHook.Data;
using VolMon.Core.Config;

namespace VolMon.GUI.Services;

/// <summary>
/// Shortcut actions the hotkey service can raise.
/// </summary>
public enum HotkeyAction
{
    VolumeUp,
    VolumeDown,
    SelectNextGroup,
    SelectPreviousGroup,
    MuteToggle
}

/// <summary>
/// A key combination: zero or more modifiers plus a single key code.
/// </summary>
internal readonly record struct KeyCombo(EventMask Modifiers, KeyCode Key)
{
    /// <summary>
    /// Matches when the given mask contains at least the required modifiers
    /// and the key code matches. Ignores extra modifiers that aren't part of
    /// the combo (e.g. NumLock, CapsLock).
    /// </summary>
    public bool Matches(EventMask currentMask, KeyCode currentKey)
    {
        if (Key == KeyCode.VcUndefined) return false;
        if (currentKey != Key) return false;

        // Check each required modifier category is present.
        // We check the generic Shift/Ctrl/Alt/Meta (left or right).
        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl() && !currentMask.HasCtrl()) return false;
        if (Modifiers.HasAlt() && !currentMask.HasAlt()) return false;
        if (Modifiers.HasMeta() && !currentMask.HasMeta()) return false;

        // Also verify no extra main modifiers are pressed that aren't required.
        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl() && currentMask.HasCtrl()) return false;
        if (!Modifiers.HasAlt() && currentMask.HasAlt()) return false;
        if (!Modifiers.HasMeta() && currentMask.HasMeta()) return false;

        return true;
    }
}

/// <summary>
/// Listens for global key presses using SharpHook and raises events
/// for configured shortcut key combinations. Uses EventLoopGlobalHook
/// so event handlers run on a dedicated thread without blocking the hook.
/// The hook captures input at the OS level and works regardless of
/// whether the application window is focused, hidden, or minimized.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly EventLoopGlobalHook _hook;
    private KeyCombo _volumeUpCombo;
    private KeyCombo _volumeDownCombo;
    private KeyCombo _selectNextCombo;
    private KeyCombo _selectPrevCombo;
    private KeyCombo _muteToggleCombo;
    private bool _disposed;

    /// <summary>
    /// Raised on the SharpHook event-loop thread when a configured hotkey is pressed.
    /// The handler is responsible for dispatching to the UI thread.
    /// </summary>
    public event Action<HotkeyAction>? HotkeyPressed;

    public GlobalHotkeyService()
    {
        // EventLoopGlobalHook runs handlers on a separate dedicated thread so
        // the hook thread is never blocked. The background thread parameter (true)
        // ensures the hook thread won't prevent app exit.
        _hook = new EventLoopGlobalHook(runAsyncOnBackgroundThread: true);
        _hook.KeyPressed += OnKeyPressed;
    }

    /// <summary>
    /// Applies shortcut configuration. Can be called to update bindings at runtime.
    /// </summary>
    public void Configure(ShortcutConfig config)
    {
        _volumeUpCombo = ParseCombo(config.VolumeUp, KeyCode.VcF13);
        _volumeDownCombo = ParseCombo(config.VolumeDown, KeyCode.VcF14);
        _selectNextCombo = ParseCombo(config.SelectNextGroup, KeyCode.VcF15);
        _selectPrevCombo = ParseCombo(config.SelectPreviousGroup, KeyCode.VcF16);
        _muteToggleCombo = ParseCombo(config.MuteToggle, KeyCode.VcF17);
    }

    /// <summary>
    /// Starts the global hook on a background thread. Call once at app startup.
    /// </summary>
    public Task StartAsync() => _hook.RunAsync();

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var mask = e.RawEvent.Mask;
        var key = e.Data.KeyCode;

        // Skip modifier-only key presses
        if (IsModifierKey(key)) return;

        if (_volumeUpCombo.Matches(mask, key))
            HotkeyPressed?.Invoke(HotkeyAction.VolumeUp);
        else if (_volumeDownCombo.Matches(mask, key))
            HotkeyPressed?.Invoke(HotkeyAction.VolumeDown);
        else if (_selectNextCombo.Matches(mask, key))
            HotkeyPressed?.Invoke(HotkeyAction.SelectNextGroup);
        else if (_selectPrevCombo.Matches(mask, key))
            HotkeyPressed?.Invoke(HotkeyAction.SelectPreviousGroup);
        else if (_muteToggleCombo.Matches(mask, key))
            HotkeyPressed?.Invoke(HotkeyAction.MuteToggle);
    }

    /// <summary>
    /// Parses a combo string like "Ctrl+Shift+F1" or just "F13" into a KeyCombo.
    /// Modifiers: Ctrl, Alt, Shift, Meta. The last segment is the key.
    /// </summary>
    internal static KeyCombo ParseCombo(string combo, KeyCode fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(combo))
            return new KeyCombo(EventMask.None, fallbackKey);

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return new KeyCombo(EventMask.None, fallbackKey);

        var modifiers = EventMask.None;
        var keyPart = parts[^1]; // Last part is the key

        // All parts except the last are modifiers
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => EventMask.LeftCtrl,
                "alt" => EventMask.LeftAlt,
                "shift" => EventMask.LeftShift,
                "meta" or "super" or "win" => EventMask.LeftMeta,
                _ => EventMask.None,
            };
        }

        var key = ParseKeyCode(keyPart, fallbackKey);
        return new KeyCombo(modifiers, key);
    }

    private static KeyCode ParseKeyCode(string name, KeyCode fallback)
    {
        // SharpHook KeyCode enum names are like "VcF13", "VcA", "VcUp", etc.
        // We accept either the raw enum name or the short form (e.g. "F13" -> "VcF13").
        if (Enum.TryParse<KeyCode>(name, ignoreCase: true, out var code))
            return code;

        if (Enum.TryParse<KeyCode>("Vc" + name, ignoreCase: true, out code))
            return code;

        return fallback;
    }

    private static bool IsModifierKey(KeyCode key) => key is
        KeyCode.VcLeftShift or KeyCode.VcRightShift or
        KeyCode.VcLeftControl or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt or
        KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.KeyPressed -= OnKeyPressed;
        _hook.Dispose();
    }
}
