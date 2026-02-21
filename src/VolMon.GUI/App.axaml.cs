using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using VolMon.Core.Config;
using VolMon.GUI.Services;
using VolMon.GUI.Services.Overlay;
using VolMon.GUI.ViewModels;
using VolMon.GUI.Views;

namespace VolMon.GUI;

public class App : Application
{
    private GlobalHotkeyService? _hotkeyService;
    private OverlayWindow? _overlayWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Set up tray icon behavior: closing the window hides it instead of exiting
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            mainWindow.Closing += (_, e) =>
            {
                // Hide instead of close (tray app behavior)
                e.Cancel = true;
                mainWindow.Hide();
            };

            // Set up tray icon
            var trayIcon = new TrayIcon
            {
                ToolTipText = "VolMon - Audio Group Manager",
                IsVisible = true,
                Icon = new WindowIcon(
                    AssetLoader.Open(new Uri("avares://VolMon.GUI/Assets/VolMonLogo.png")))
            };

            trayIcon.Clicked += (_, _) =>
            {
                if (mainWindow.IsVisible)
                    mainWindow.Hide();
                else
                    mainWindow.Show();
            };

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("Show/Hide");
            showItem.Click += (_, _) =>
            {
                if (mainWindow.IsVisible)
                    mainWindow.Hide();
                else
                    mainWindow.Show();
            };

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) =>
            {
                _hotkeyService?.Dispose();
                desktop.Shutdown();
            };

            menu.Add(showItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            trayIcon.Menu = menu;

            // ── Global hotkeys + overlay ─────────────────────────────
            InitializeHotkeys(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void InitializeHotkeys(MainWindow mainWindow)
    {
        // Load config to get shortcut bindings
        var configManager = new ConfigManager();
        try { await configManager.LoadAsync(); }
        catch { /* use defaults if config can't be loaded */ }

        var shortcuts = configManager.Config.Shortcuts;

        // Detect platform and create the appropriate overlay helper
        var overlayHelper = PlatformOverlayFactory.Create();

        // Create and warm up the overlay window. WarmUp() briefly shows it off-screen
        // so the platform handle is created, then hides it. This avoids a slow first-show.
        _overlayWindow = new OverlayWindow(overlayHelper);
        _overlayWindow.WarmUp();

        // Wire up MainViewModel's overlay event
        if (mainWindow.DataContext is MainViewModel vm)
        {
            vm.OverlayRequested += (name, volume, muted, color) =>
            {
                Dispatcher.UIThread.Post(() =>
                    _overlayWindow.ShowOverlay(name, volume, muted, color));
            };

            // Create and start global hotkey service
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Configure(shortcuts);

            _hotkeyService.HotkeyPressed += action =>
            {
                // SharpHook fires on its own thread — dispatch to UI
                Dispatcher.UIThread.Post(async () =>
                    await vm.HandleHotkeyAsync(action));
            };

            // Reconfigure hotkey service when shortcuts are changed in settings
            vm.ShortcutsChanged += newConfig =>
            {
                _hotkeyService.Configure(newConfig);
            };

            try { await _hotkeyService.StartAsync(); }
            catch { /* hook may fail in some environments (e.g. Wayland without permissions) */ }
        }
    }
}
