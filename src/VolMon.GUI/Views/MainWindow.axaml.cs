using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VolMon.Core.Audio;
using VolMon.GUI.ViewModels;

namespace VolMon.GUI.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private ShortcutBindingViewModel? _listeningBinding;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(Button.ClickEvent, OnButtonClick, RoutingStrategies.Bubble);
    }

    // ── Connection lifecycle ─────────────────────────────────────────
    // The MainViewModel manages its own persistent connection to the daemon
    // (with auto-reconnect). No polling timer is needed — state updates are
    // pushed by the daemon over the duplex IPC connection.

    // ── Pointer pressed dispatch ─────────────────────────────────────

    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var source = e.Source as Control;

        // Never intercept interactive controls (sliders, buttons, etc.)
        if (IsInsideInteractiveControl(source))
            return;

        // ── Color bar click → show color picker
        var colorBar = FindParentWithClass(source, "colorBar");
        if (colorBar is not null)
        {
            var col = FindParentWithClass(colorBar, "groupColumn");
            if (col?.DataContext is GroupColumnViewModel vm)
                ShowColorPicker(colorBar, vm);
            e.Handled = true;
            return;
        }

        // ── Ellipsis menu click → show group menu
        var groupMenu = FindParentWithClass(source, "groupMenu");
        if (groupMenu is not null)
        {
            var col = FindParentWithClass(groupMenu, "groupColumn");
            if (col?.DataContext is GroupColumnViewModel vm)
                ShowGroupMenu(groupMenu, vm);
            e.Handled = true;
            return;
        }

        // ── Group drag handle → group reorder
        var dragHandle = FindParentWithClass(source, "groupDragHandle");
        if (dragHandle is not null)
        {
            var col = FindParentWithClass(dragHandle, "groupColumn");
            if (col?.Tag is Guid groupId)
            {
                var data = new DataObject();
                data.Set("VolMon-DragType", "group-reorder");
                data.Set("VolMon-GroupId", groupId.ToString());
                e.Handled = true;
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            return;
        }

        // ── Pool item or group member → item drag
        var border = FindParentWithClass(source, "draggablePool")
                  ?? FindParentWithClass(source, "draggableMember");
        if (border is null) return;

        var itemData = new DataObject();

        if (border.Tag is PoolItemViewModel poolItem)
        {
            itemData.Set("VolMon-ItemType", poolItem.ItemType);
            itemData.Set("VolMon-Identifier", poolItem.Identifier);
            itemData.Set("VolMon-Source", "pool");
        }
        else if (border.Tag is GroupMemberViewModel member)
        {
            itemData.Set("VolMon-ItemType", member.ItemType);
            itemData.Set("VolMon-Identifier", member.Identifier);
            itemData.Set("VolMon-Source", member.GroupId.ToString());
        }
        else
        {
            return;
        }

        e.Handled = true;
        await DragDrop.DoDragDrop(e, itemData, DragDropEffects.Move);
    }

    // ── Drag over ────────────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = e.Source as Control;
        var groupCol = FindParentWithClass(target, "groupColumn");
        var pool = FindParentWithClass(target, "poolArea");

        e.DragEffects = (groupCol is not null || pool is not null)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    // ── Drop handling ────────────────────────────────────────────────

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null) return;

        // Group reorder
        if (e.Data.Contains("VolMon-DragType") &&
            e.Data.Get("VolMon-DragType") as string == "group-reorder")
        {
            var srcIdStr = e.Data.Get("VolMon-GroupId") as string;
            if (srcIdStr is null || !Guid.TryParse(srcIdStr, out var srcId)) return;

            var target = e.Source as Control;
            var col = FindParentWithClass(target, "groupColumn");
            if (col?.Tag is Guid tgtId && srcId != tgtId)
                await _viewModel.ReorderGroupAsync(srcId, tgtId);
            return;
        }

        // Item drop
        if (!e.Data.Contains("VolMon-ItemType")) return;

        var itemType = e.Data.Get("VolMon-ItemType") as string;
        var identifier = e.Data.Get("VolMon-Identifier") as string;
        var sourceGroup = e.Data.Get("VolMon-Source") as string;
        if (itemType is null || identifier is null || sourceGroup is null) return;

        var dropTarget = e.Source as Control;

        // Dropped onto a group column
        var targetCol = FindParentWithClass(dropTarget, "groupColumn");
        if (targetCol?.Tag is Guid targetGroupId)
        {
            if (sourceGroup == "pool" || !Guid.TryParse(sourceGroup, out var srcGroupId) || srcGroupId != targetGroupId)
                await _viewModel.AssignToGroupAsync(targetGroupId, itemType, identifier);
            return;
        }

        // Dropped onto a pool area
        var poolArea = FindParentWithClass(dropTarget, "poolArea");
        if (poolArea is not null && sourceGroup != "pool" && Guid.TryParse(sourceGroup, out var poolSrcId))
            await _viewModel.ReturnToPoolAsync(poolSrcId, itemType, identifier);
    }

    // ── Color picker flyout ──────────────────────────────────────────

    private static void ShowColorPicker(Control anchor, GroupColumnViewModel groupVm)
    {
        var wrap = new WrapPanel { Width = 160 };
        Flyout? flyout = null;

        foreach (var color in GroupColumnViewModel.AvailableColors)
        {
            IBrush brush;
            try { brush = SolidColorBrush.Parse(color); }
            catch { continue; }

            var btn = new Button
            {
                Width = 24, Height = 24,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = brush,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(color == groupVm.ColorHex ? 2 : 0),
                BorderBrush = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };

            var c = color;
            btn.Click += (_, _) =>
            {
                groupVm.ChangeColorCommand.Execute(c).Subscribe();
                flyout?.Hide();
            };
            wrap.Children.Add(btn);
        }

        flyout = new Flyout
        {
            Content = new Border
            {
                Background = SolidColorBrush.Parse("#333"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Child = wrap,
            },
            Placement = PlacementMode.Bottom,
        };
        flyout.ShowAt(anchor);
    }

    // ── Group ellipsis menu ──────────────────────────────────────────

    private void ShowGroupMenu(Control anchor, GroupColumnViewModel groupVm)
    {
        Flyout? flyout = null;

        var renameBtn = new Button
        {
            Content = "Rename",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#CCC"),
            Padding = new Thickness(8, 4),
        };
        renameBtn.Click += (_, _) =>
        {
            flyout?.Hide();
            DispatcherTimer.RunOnce(() => ShowRenameFlyout(anchor, groupVm),
                TimeSpan.FromMilliseconds(50));
        };

        var defaultLabel = groupVm.IsDefault ? "Unset Default" : "Set as Default";
        var defaultBtn = new Button
        {
            Content = defaultLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#CCC"),
            Padding = new Thickness(8, 4),
        };
        defaultBtn.Click += (_, _) =>
        {
            flyout?.Hide();
            groupVm.ToggleDefault();
        };

        var skipLabel = groupVm.SkipShortcut ? "Include in Shortcuts" : "Skip in Shortcuts";
        var skipBtn = new Button
        {
            Content = skipLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#CCC"),
            Padding = new Thickness(8, 4),
        };
        skipBtn.Click += (_, _) =>
        {
            flyout?.Hide();
            groupVm.ToggleSkipShortcut();
        };

        // Mode toggle: Direct (⚡) ↔ Compatibility (🛡)
        // Only PulseAudio/PipeWire (Linux) supports virtual null-sinks.
        Button? modeBtn = null;
        if (OperatingSystem.IsLinux())
        {
            var modeIsCompat = groupVm.Mode == GroupMode.Compatibility;
            var modeLabel = modeIsCompat
                ? "\uD83D\uDEE1 Compatibility Mode (on)"
                : "\u26A1 Direct Mode (on)";
            modeBtn = new Button
            {
                Content = modeLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Foreground = modeIsCompat
                    ? SolidColorBrush.Parse("#4FC3F7")
                    : SolidColorBrush.Parse("#CCC"),
                Padding = new Thickness(8, 4),
            };
            ToolTip.SetTip(modeBtn, modeIsCompat
                ? "Compatibility Mode: streams are routed through a virtual device.\nThe app's own volume slider has no effect."
                : "Direct Mode: VolMon sets the stream volume directly.\nSwitch to Compatibility Mode to prevent app volume sliders from interfering.");
            modeBtn.Click += (_, _) =>
            {
                flyout?.Hide();
                groupVm.ToggleMode();
            };
        }

        var deleteBtn = new Button
        {
            Content = "Delete",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#E63946"),
            Padding = new Thickness(8, 4),
        };
        deleteBtn.Click += (_, _) =>
        {
            flyout?.Hide();
            groupVm.DeleteCommand.Execute().Subscribe();
        };

        var menuPanel = new StackPanel { Width = 150, Spacing = 2 };
        menuPanel.Children.Add(renameBtn);
        menuPanel.Children.Add(defaultBtn);
        menuPanel.Children.Add(skipBtn);
        if (modeBtn is not null)
            menuPanel.Children.Add(modeBtn);
        menuPanel.Children.Add(deleteBtn);

        flyout = new Flyout
        {
            Content = menuPanel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        flyout.ShowAt(anchor);
    }

    private void ShowRenameFlyout(Control anchor, GroupColumnViewModel groupVm)
    {
        Flyout? flyout = null;

        var textBox = new TextBox
        {
            Text = groupVm.Name,
            Width = 140,
            FontSize = 12,
        };

        var okBtn = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 4),
            Margin = new Thickness(0, 4, 0, 0),
        };

        async void Commit()
        {
            var newName = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != groupVm.Name && _viewModel is not null)
                await _viewModel.RenameGroupAsync(groupVm.Id, newName);
            flyout?.Hide();
        }

        okBtn.Click += (_, _) => Commit();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { flyout?.Hide(); e.Handled = true; }
        };

        flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 160,
                Children = { textBox, okBtn }
            },
            Placement = PlacementMode.Bottom,
        };
        flyout.ShowAt(anchor);

        // Focus and select all text after the flyout renders
        DispatcherTimer.RunOnce(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, TimeSpan.FromMilliseconds(50));
    }

    // ── Shortcut button routing ─────────────────────────────────────

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button btn) return;

        if (btn.Classes.Contains("shortcutKey") && btn.Tag is ShortcutBindingViewModel keyBinding)
        {
            OnShortcutKeyClick(keyBinding);
            e.Handled = true;
        }
        else if (btn.Classes.Contains("shortcutClear") && btn.Tag is ShortcutBindingViewModel clearBinding)
        {
            OnShortcutClearClick(clearBinding);
            e.Handled = true;
        }
    }

    // ── Shortcut key capture ────────────────────────────────────────

    private void OnShortcutKeyClick(ShortcutBindingViewModel binding)
    {
        // Cancel any previous listening and restore its display
        if (_listeningBinding is not null)
        {
            _listeningBinding.IsListening = false;
            // Restore display from the stored key code
            _listeningBinding.KeyDisplay = ShortcutBindingViewModel.FormatKey(_listeningBinding.KeyCode);
        }

        binding.IsListening = true;
        binding.KeyDisplay = "Press a key...";
        _listeningBinding = binding;

        // Suppress global hotkeys while rebinding
        _viewModel?.NotifyShortcutListening(true);
    }

    private async void OnShortcutClearClick(ShortcutBindingViewModel binding)
    {
        // Cancel listening if active
        if (_listeningBinding is not null)
        {
            _listeningBinding.IsListening = false;
            _listeningBinding.KeyDisplay = ShortcutBindingViewModel.FormatKey(_listeningBinding.KeyCode);
            _listeningBinding = null;

            // Re-enable global hotkeys
            _viewModel?.NotifyShortcutListening(false);
        }

        if (_viewModel is not null)
            await _viewModel.ClearShortcutAsync(binding);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (_listeningBinding is not null)
        {
            // Map Avalonia Key to a SharpHook-compatible key code name
            var keyName = MapAvaloniaKeyToSharpHook(e.Key);
            if (keyName is not null)
            {
                // Build combo string with modifiers: "Ctrl+Shift+F1"
                var combo = BuildComboString(e.KeyModifiers, keyName);
                _listeningBinding.KeyCode = combo; // setter updates KeyDisplay
                _listeningBinding.IsListening = false;
                _listeningBinding = null;

                // Re-enable global hotkeys
                _viewModel?.NotifyShortcutListening(false);

                if (_viewModel is not null)
                    await _viewModel.SaveShortcutsAsync();
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_listeningBinding is not null)
        {
            var point = e.GetCurrentPoint(this);
            var mouseToken = MapPointerButtonToToken(point.Properties);

            if (mouseToken is not null)
            {
                // Build combo string with keyboard modifiers: e.g. "Ctrl+Mouse4"
                var combo = BuildComboString(e.KeyModifiers, mouseToken);
                _listeningBinding.KeyCode = combo;
                _listeningBinding.IsListening = false;
                _listeningBinding = null;

                // Re-enable global hotkeys
                _viewModel?.NotifyShortcutListening(false);

                if (_viewModel is not null)
                    await _viewModel.SaveShortcutsAsync();

                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_listeningBinding is not null)
        {
            var token = MapWheelDeltaToToken(e.Delta);
            if (token is not null)
            {
                var combo = BuildComboString(e.KeyModifiers, token);
                _listeningBinding.KeyCode = combo;
                _listeningBinding.IsListening = false;
                _listeningBinding = null;

                _viewModel?.NotifyShortcutListening(false);

                if (_viewModel is not null)
                    await _viewModel.SaveShortcutsAsync();

                e.Handled = true;
                return;
            }
        }

        base.OnPointerWheelChanged(e);
    }

    /// <summary>
    /// Maps an Avalonia wheel delta vector to a "WheelXxx" token.
    /// Avalonia Delta: positive Y = up, negative Y = down; positive X = right, negative X = left.
    /// </summary>
    private static string? MapWheelDeltaToToken(Vector delta)
    {
        // Prefer the axis with the larger absolute movement to handle diagonal scrolling.
        if (Math.Abs(delta.Y) >= Math.Abs(delta.X))
        {
            if (delta.Y > 0) return "WheelUp";
            if (delta.Y < 0) return "WheelDown";
        }
        else
        {
            if (delta.X > 0) return "WheelRight";
            if (delta.X < 0) return "WheelLeft";
        }
        return null;
    }

    /// <summary>
    /// Maps an Avalonia pointer button to the "MouseN" token used in combo strings.
    /// </summary>
    private static string? MapPointerButtonToToken(PointerPointProperties props)
    {
        if (props.IsXButton1Pressed) return "Mouse4";
        if (props.IsXButton2Pressed) return "Mouse5";
        if (props.IsMiddleButtonPressed) return "Mouse3";
        if (props.IsLeftButtonPressed) return "Mouse1";
        if (props.IsRightButtonPressed) return "Mouse2";
        return null;
    }

    /// <summary>
    /// Builds a combo string like "Ctrl+Shift+F1" from Avalonia key modifiers and a key name.
    /// Modifier order: Ctrl, Alt, Shift, Meta.
    /// </summary>
    private static string BuildComboString(KeyModifiers modifiers, string keyName)
    {
        var parts = new List<string>(4);

        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Meta");

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    /// <summary>
    /// Maps an Avalonia Key enum to a SharpHook KeyCode name string.
    /// Returns null for modifier-only keys and unsupported keys.
    /// </summary>
    private static string? MapAvaloniaKeyToSharpHook(Key key) => key switch
    {
        // Function keys
        Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
        Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
        Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
        Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15",
        Key.F16 => "F16", Key.F17 => "F17", Key.F18 => "F18",
        Key.F19 => "F19", Key.F20 => "F20", Key.F21 => "F21",
        Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",

        // Letters
        Key.A => "A", Key.B => "B", Key.C => "C", Key.D => "D",
        Key.E => "E", Key.F => "F", Key.G => "G", Key.H => "H",
        Key.I => "I", Key.J => "J", Key.K => "K", Key.L => "L",
        Key.M => "M", Key.N => "N", Key.O => "O", Key.P => "P",
        Key.Q => "Q", Key.R => "R", Key.S => "S", Key.T => "T",
        Key.U => "U", Key.V => "V", Key.W => "W", Key.X => "X",
        Key.Y => "Y", Key.Z => "Z",

        // Numbers
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3",
        Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7",
        Key.D8 => "8", Key.D9 => "9",

        // Numpad digits
        Key.NumPad0 => "NumPad0", Key.NumPad1 => "NumPad1",
        Key.NumPad2 => "NumPad2", Key.NumPad3 => "NumPad3",
        Key.NumPad4 => "NumPad4", Key.NumPad5 => "NumPad5",
        Key.NumPad6 => "NumPad6", Key.NumPad7 => "NumPad7",
        Key.NumPad8 => "NumPad8", Key.NumPad9 => "NumPad9",

        // Numpad operators
        Key.Multiply => "NumPadMultiply",
        Key.Divide   => "NumPadDivide",
        Key.Subtract => "NumPadSubtract",
        Key.Add      => "NumPadAdd",
        Key.Decimal  => "NumPadDecimal",

        // Navigation
        Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
        Key.Home => "Home", Key.End => "End",
        Key.PageUp => "PageUp", Key.PageDown => "PageDown",
        Key.Insert => "Insert", Key.Delete => "Delete",

        // Common keys
        Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab",
        Key.Back => "Backspace", Key.Escape => "Escape",
        Key.Pause => "Pause", Key.Scroll => "ScrollLock",
        Key.PrintScreen => "PrintScreen",

        // Punctuation/symbols
        Key.OemMinus => "Minus", Key.OemPlus => "Equals",
        Key.OemOpenBrackets => "OpenBracket", Key.OemCloseBrackets => "CloseBracket",
        Key.OemBackslash or Key.OemPipe => "Backslash", Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote", Key.OemComma => "Comma",
        Key.OemPeriod => "Period", Key.OemQuestion => "Slash",
        Key.OemTilde => "BackQuote",

        // Modifier-only keys — skip these
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => null,

        _ => null,
    };

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool IsInsideInteractiveControl(Control? control)
    {
        var c = control;
        while (c is not null)
        {
            if (c is Slider or Track or Thumb or RepeatButton
                or Button or CheckBox or TextBox
                or ScrollBar)
                return true;
            c = c.GetVisualParent() as Control;
        }
        return false;
    }

    private static Control? FindParentWithClass(Control? control, string className)
    {
        while (control is not null)
        {
            if (control.Classes.Contains(className))
                return control;
            control = control.GetVisualParent() as Control;
        }
        return null;
    }
}
