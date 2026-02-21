using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using VolMon.GUI.Views;

namespace VolMon.GUI;

public class App : Application
{
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
                desktop.Shutdown();
            };

            menu.Add(showItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            trayIcon.Menu = menu;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
