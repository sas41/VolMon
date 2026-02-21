using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VolMon.GUI.ViewModels;

namespace VolMon.GUI.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private DispatcherTimer? _pollTimer;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
    }

    // ── Auto-refresh timer ───────────────────────────────────────────

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        StartPolling();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
                StartPolling();
            else
                StopPolling();
        }
    }

    private void StartPolling()
    {
        if (_pollTimer is not null) return;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null)
                await _viewModel.PollAndRefreshAsync();
        };
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

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
            // Short delay to let the menu close before the rename flyout opens
            DispatcherTimer.RunOnce(() => ShowRenameFlyout(anchor, groupVm),
                TimeSpan.FromMilliseconds(50));
        };

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

        flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 110,
                Spacing = 2,
                Children = { renameBtn, deleteBtn }
            },
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

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool IsInsideInteractiveControl(Control? control)
    {
        var c = control;
        while (c is not null)
        {
            if (c is Slider or Track or Thumb or RepeatButton
                or Button or CheckBox or RadioButton or TextBox
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
