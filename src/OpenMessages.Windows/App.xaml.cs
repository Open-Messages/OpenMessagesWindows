using System.Windows;
using OpenMessages.Core;

namespace OpenMessages.Windows;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\OpenMessages.Windows", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("OpenMessages is already running. Find it in the notification area.", "OpenMessages");
            Shutdown();
            return;
        }

        var sync = new OtpSyncService(new DpapiStore(), AppConfig.Load);
        var model = new MainViewModel(sync);
        _window = new MainWindow(model);
        _tray = new TrayIcon(model, ShowWindow, Quit);
        if (!e.Args.Contains("--minimized")) ShowWindow();
        await sync.StartAsync();
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _window?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
