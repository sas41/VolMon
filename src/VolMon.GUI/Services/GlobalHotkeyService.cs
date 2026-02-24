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
/// The four wheel directions used in combo tokens.
/// </summary>
internal enum WheelDirection { None, Up, Down, Left, Right }

/// <summary>
/// A keyboard combination: zero or more modifiers plus a single key code.
/// </summary>
internal readonly record struct KeyCombo(EventMask Modifiers, KeyCode Key)
{
    public bool Matches(EventMask currentMask, KeyCode currentKey)
    {
        if (Key == KeyCode.VcUndefined) return false;
        if (currentKey != Key) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// A mouse-button combination: zero or more keyboard modifiers plus a mouse button.
/// <see cref="MouseButton.NoButton"/> means the combo is unset.
/// </summary>
internal readonly record struct MouseButtonCombo(EventMask Modifiers, MouseButton Button)
{
    public bool Matches(EventMask currentMask, MouseButton currentButton)
    {
        if (Button == MouseButton.NoButton) return false;
        if (currentButton != Button) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// A scroll-wheel combination: zero or more keyboard modifiers plus a wheel direction.
/// <see cref="WheelDirection.None"/> means the combo is unset.
/// </summary>
internal readonly record struct WheelCombo(EventMask Modifiers, WheelDirection Direction)
{
    public bool Matches(EventMask currentMask, WheelDirection currentDir)
    {
        if (Direction == WheelDirection.None) return false;
        if (currentDir != Direction) return false;

        if (Modifiers.HasShift() && !currentMask.HasShift()) return false;
        if (Modifiers.HasCtrl()  && !currentMask.HasCtrl())  return false;
        if (Modifiers.HasAlt()   && !currentMask.HasAlt())   return false;
        if (Modifiers.HasMeta()  && !currentMask.HasMeta())  return false;

        if (!Modifiers.HasShift() && currentMask.HasShift()) return false;
        if (!Modifiers.HasCtrl()  && currentMask.HasCtrl())  return false;
        if (!Modifiers.HasAlt()   && currentMask.HasAlt())   return false;
        if (!Modifiers.HasMeta()  && currentMask.HasMeta())  return false;

        return true;
    }
}

/// <summary>
/// Listens for global key, mouse-button, and scroll-wheel events using SharpHook and
/// raises events for configured shortcut combinations. Uses EventLoopGlobalHook so
/// event handlers run on a dedicated thread without blocking the hook.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly EventLoopGlobalHook _hook;

    private KeyCombo         _volumeUpKey;
    private KeyCombo         _volumeDownKey;
    private KeyCombo         _selectNextKey;
    private KeyCombo         _selectPrevKey;
    private KeyCombo         _muteToggleKey;

    private MouseButtonCombo _volumeUpMouse;
    private MouseButtonCombo _volumeDownMouse;
    private MouseButtonCombo _selectNextMouse;
    private MouseButtonCombo _selectPrevMouse;
    private MouseButtonCombo _muteToggleMouse;

    private WheelCombo       _volumeUpWheel;
    private WheelCombo       _volumeDownWheel;
    private WheelCombo       _selectNextWheel;
    private WheelCombo       _selectPrevWheel;
    private WheelCombo       _muteToggleWheel;

    private bool _disposed;

    /// <summary>
    /// When true, all hotkey matching is suppressed. Set while the user is
    /// actively rebinding a shortcut so that existing bindings do not fire.
    /// </summary>
    public volatile bool IsListening;

    /// <summary>
    /// Raised on the SharpHook event-loop thread when a configured hotkey fires.
    /// The handler is responsible for dispatching to the UI thread.
    /// </summary>
    public event Action<HotkeyAction>? HotkeyPressed;

    public GlobalHotkeyService()
    {
        _hook = new EventLoopGlobalHook(runAsyncOnBackgroundThread: true);
        _hook.KeyPressed   += OnKeyPressed;
        _hook.MousePressed += OnMousePressed;
        _hook.MouseWheel   += OnMouseWheel;
    }

    /// <summary>
    /// Applies shortcut configuration. Can be called to update bindings at runtime.
    /// </summary>
    public void Configure(ShortcutConfig config)
    {
        ParseBinding(config.VolumeUp,             KeyCode.VcF13, out _volumeUpKey,    out _volumeUpMouse,    out _volumeUpWheel);
        ParseBinding(config.VolumeDown,           KeyCode.VcF14, out _volumeDownKey,  out _volumeDownMouse,  out _volumeDownWheel);
        ParseBinding(config.SelectNextGroup,      KeyCode.VcF15, out _selectNextKey,  out _selectNextMouse,  out _selectNextWheel);
        ParseBinding(config.SelectPreviousGroup,  KeyCode.VcF16, out _selectPrevKey,  out _selectPrevMouse,  out _selectPrevWheel);
        ParseBinding(config.MuteToggle,           KeyCode.VcF17, out _muteToggleKey,  out _muteToggleMouse,  out _muteToggleWheel);
    }

    public Task StartAsync() => _hook.RunAsync();

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var key  = e.Data.KeyCode;

        if (IsModifierKey(key)) return;

        if      (_volumeUpKey.Matches(mask, key))    HotkeyPressed?.Invoke(HotkeyAction.VolumeUp);
        else if (_volumeDownKey.Matches(mask, key))   HotkeyPressed?.Invoke(HotkeyAction.VolumeDown);
        else if (_selectNextKey.Matches(mask, key))   HotkeyPressed?.Invoke(HotkeyAction.SelectNextGroup);
        else if (_selectPrevKey.Matches(mask, key))   HotkeyPressed?.Invoke(HotkeyAction.SelectPreviousGroup);
        else if (_muteToggleKey.Matches(mask, key))   HotkeyPressed?.Invoke(HotkeyAction.MuteToggle);
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var btn  = e.Data.Button;

        if      (_volumeUpMouse.Matches(mask, btn))   HotkeyPressed?.Invoke(HotkeyAction.VolumeUp);
        else if (_volumeDownMouse.Matches(mask, btn))  HotkeyPressed?.Invoke(HotkeyAction.VolumeDown);
        else if (_selectNextMouse.Matches(mask, btn))  HotkeyPressed?.Invoke(HotkeyAction.SelectNextGroup);
        else if (_selectPrevMouse.Matches(mask, btn))  HotkeyPressed?.Invoke(HotkeyAction.SelectPreviousGroup);
        else if (_muteToggleMouse.Matches(mask, btn))  HotkeyPressed?.Invoke(HotkeyAction.MuteToggle);
    }

    private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
    {
        if (IsListening) return;

        var mask = e.RawEvent.Mask;
        var dir  = GetWheelDirection(e.Data);

        if      (_volumeUpWheel.Matches(mask, dir))    HotkeyPressed?.Invoke(HotkeyAction.VolumeUp);
        else if (_volumeDownWheel.Matches(mask, dir))   HotkeyPressed?.Invoke(HotkeyAction.VolumeDown);
        else if (_selectNextWheel.Matches(mask, dir))   HotkeyPressed?.Invoke(HotkeyAction.SelectNextGroup);
        else if (_selectPrevWheel.Matches(mask, dir))   HotkeyPressed?.Invoke(HotkeyAction.SelectPreviousGroup);
        else if (_muteToggleWheel.Matches(mask, dir))   HotkeyPressed?.Invoke(HotkeyAction.MuteToggle);
    }

    /// <summary>
    /// Derives a <see cref="WheelDirection"/> from raw wheel event data.
    /// Positive rotation = up / left; negative = down / right.
    /// </summary>
    internal static WheelDirection GetWheelDirection(MouseWheelEventData data)
    {
        if (data.Rotation == 0) return WheelDirection.None;

        return data.Direction == MouseWheelScrollDirection.Horizontal
            ? (data.Rotation > 0 ? WheelDirection.Left  : WheelDirection.Right)
            : (data.Rotation > 0 ? WheelDirection.Up    : WheelDirection.Down);
    }

    /// <summary>
    /// Parses a combo string into a KeyCombo, MouseButtonCombo, or WheelCombo.
    /// Wheel tokens: WheelUp, WheelDown, WheelLeft, WheelRight.
    /// Mouse tokens: Mouse1–Mouse5.
    /// </summary>
    private static void ParseBinding(string combo, KeyCode fallbackKey,
        out KeyCombo keyCombo, out MouseButtonCombo mouseCombo, out WheelCombo wheelCombo)
    {
        keyCombo   = default;
        mouseCombo = default;
        wheelCombo = default;

        if (string.IsNullOrWhiteSpace(combo))
        {
            keyCombo = new KeyCombo(EventMask.None, fallbackKey);
            return;
        }

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            keyCombo = new KeyCombo(EventMask.None, fallbackKey);
            return;
        }

        var modifiers = EventMask.None;
        var keyPart   = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control"        => EventMask.LeftCtrl,
                "alt"                      => EventMask.LeftAlt,
                "shift"                    => EventMask.LeftShift,
                "meta" or "super" or "win" => EventMask.LeftMeta,
                _                          => EventMask.None,
            };
        }

        // Wheel token?
        var wheelDir = ParseWheelDirection(keyPart);
        if (wheelDir != WheelDirection.None)
        {
            wheelCombo = new WheelCombo(modifiers, wheelDir);
            return;
        }

        // Mouse button token?
        var mouseButton = ParseMouseButton(keyPart);
        if (mouseButton != MouseButton.NoButton)
        {
            mouseCombo = new MouseButtonCombo(modifiers, mouseButton);
            return;
        }

        keyCombo = new KeyCombo(modifiers, ParseKeyCode(keyPart, fallbackKey));
    }

    /// <summary>
    /// Parses "WheelUp", "WheelDown", "WheelLeft", "WheelRight" tokens.
    /// Returns <see cref="WheelDirection.None"/> for anything else.
    /// </summary>
    internal static WheelDirection ParseWheelDirection(string token) =>
        token.ToLowerInvariant() switch
        {
            "wheelup"    => WheelDirection.Up,
            "wheeldown"  => WheelDirection.Down,
            "wheelleft"  => WheelDirection.Left,
            "wheelright" => WheelDirection.Right,
            _            => WheelDirection.None,
        };

    /// <summary>
    /// Parses "Mouse1"–"Mouse5" tokens into <see cref="MouseButton"/> values.
    /// Returns <see cref="MouseButton.NoButton"/> for anything else.
    /// </summary>
    internal static MouseButton ParseMouseButton(string token) =>
        token.ToLowerInvariant() switch
        {
            "mouse1" => MouseButton.Button1,
            "mouse2" => MouseButton.Button2,
            "mouse3" => MouseButton.Button3,
            "mouse4" => MouseButton.Button4,
            "mouse5" => MouseButton.Button5,
            _        => MouseButton.NoButton,
        };

    private static KeyCode ParseKeyCode(string name, KeyCode fallback)
    {
        if (Enum.TryParse<KeyCode>(name, ignoreCase: true, out var code))
            return code;

        if (Enum.TryParse<KeyCode>("Vc" + name, ignoreCase: true, out code))
            return code;

        return fallback;
    }

    private static bool IsModifierKey(KeyCode key) => key is
        KeyCode.VcLeftShift    or KeyCode.VcRightShift   or
        KeyCode.VcLeftControl  or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt      or KeyCode.VcRightAlt     or
        KeyCode.VcLeftMeta     or KeyCode.VcRightMeta;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.KeyPressed   -= OnKeyPressed;
        _hook.MousePressed -= OnMousePressed;
        _hook.MouseWheel   -= OnMouseWheel;
        _hook.Dispose();
    }
}
