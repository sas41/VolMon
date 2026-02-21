using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using VolMon.GUI.Services.Overlay;

namespace VolMon.GUI.Views;

/// <summary>
/// A transparent, topmost, non-focusable overlay window that displays
/// at center-bottom of the active screen. Lives independently of the main window.
///
/// Platform-specific behavior (rendering above fullscreen games, detecting the
/// active monitor) is delegated to an <see cref="IPlatformOverlayHelper"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly IPlatformOverlayHelper _platformHelper;
    private DispatcherTimer? _hideTimer;
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(2);

    /// <summary>Fixed inner width of the volume bar in DIPs.</summary>
    private const double BarWidth = 228;

    /// <summary>
    /// Parameterless constructor for XAML designer/loader compatibility.
    /// At runtime, use <see cref="OverlayWindow(IPlatformOverlayHelper)"/> instead.
    /// </summary>
    public OverlayWindow() : this(PlatformOverlayFactory.Create()) { }

    public OverlayWindow(IPlatformOverlayHelper platformHelper)
    {
        _platformHelper = platformHelper;

        InitializeComponent();

        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;

        // Prevent the overlay from being closed — only hide it.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>
    /// Pre-warms the window by delegating to the platform helper so it gets a
    /// platform handle. This ensures the first real ShowOverlay() doesn't have
    /// a slow first-map penalty.
    /// </summary>
    public void WarmUp()
    {
        _platformHelper.WarmUp(this, () => { });
    }

    /// <summary>
    /// Shows the overlay with the given group info, auto-hiding after 2 seconds.
    /// Must be called on the UI thread.
    /// </summary>
    public void ShowOverlay(string groupName, int volume, bool muted, string colorHex)
    {
        // Update content
        GroupNameText.Text = groupName;
        VolumeText.Text = muted ? $"{volume}% (Muted)" : $"{volume}%";
        MutedIndicator.IsVisible = muted;

        // Update volume bar width (fixed bar track = BarWidth)
        var fraction = volume / 100.0;
        VolumeFill.Width = Math.Max(0, BarWidth * fraction);

        // Update volume bar color to match group color (dimmed if muted)
        try
        {
            var brush = SolidColorBrush.Parse(colorHex);
            if (muted) brush.Opacity = 0.4;
            VolumeFill.Background = brush;
        }
        catch { VolumeFill.Background = SolidColorBrush.Parse("#4A90D9"); }

        // Position at center-bottom of the screen the cursor is on
        PositionCenterBottom();

        Topmost = true;

        // Show the window
        if (!IsVisible)
            Show();

        // Apply platform-specific hints and raise after the window is mapped.
        // Delayed because the window needs to be fully mapped first.
        DispatcherTimer.RunOnce(() =>
        {
            _platformHelper.ApplyOverlayHints(this);
            _platformHelper.RaiseToTop(this);

            // Second raise after a bit more time — some compositors need
            // an extra kick after hints are applied
            DispatcherTimer.RunOnce(() =>
            {
                _platformHelper.RaiseToTop(this);
            }, TimeSpan.FromMilliseconds(50));
        }, TimeSpan.FromMilliseconds(30));

        // Reset auto-hide timer
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        _hideTimer.Start();
    }

    private void PositionCenterBottom()
    {
        // Primary: ask the compositor which output the cursor is on (works on
        // Wayland even when no XWayland window has focus).
        // Fallback: query cursor position and match against screen bounds.
        // Last resort: primary screen.
        var screen = FindScreenByOutputName()
                  ?? _platformHelper.FindScreenAtCursor(Screens)
                  ?? Screens.Primary;
        if (screen is null) return;

        var bounds = screen.Bounds;
        var scaling = screen.Scaling;

        var windowWidthPx = (int)(Width * scaling);
        var windowHeightPx = (int)(Height * scaling);

        Position = new PixelPoint(
            bounds.X + (bounds.Width - windowWidthPx) / 2,
            bounds.Y + bounds.Height - windowHeightPx - (int)(60 * scaling)
        );
    }

    /// <summary>
    /// Asks the platform helper for the active output name and matches it
    /// against Avalonia's screen list.
    /// </summary>
    private Screen? FindScreenByOutputName()
    {
        try
        {
            var outputName = _platformHelper.GetActiveOutputName();
            if (outputName is null) return null;

            foreach (var screen in Screens.All)
            {
                if (string.Equals(screen.DisplayName, outputName, StringComparison.OrdinalIgnoreCase))
                    return screen;
            }
        }
        catch { /* fall through */ }

        return null;
    }
}
