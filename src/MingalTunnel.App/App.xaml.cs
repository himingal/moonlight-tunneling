using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MingalTunnel.App.Services;
using MingalTunnel.App.ViewModels;
using MingalTunnel.Core;

namespace MingalTunnel.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool autostart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

        _instance = new Mutex(true, "MingalTunnel.SingleInstance", out bool first);
        if (!first)
        {
            // Second launch just brings the running window forward.
            try { EventWaitHandle.OpenExisting("MingalTunnel.Activate").Set(); } catch { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "MingalTunnel.Activate");
        new Thread(() =>
        {
            while (_activate.WaitOne()) Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, a) =>
        {
            AppLog.Error("Erro inesperado: " + a.Exception.Message);
            a.Handled = true;
        };
        SessionEnding += (_, _) =>
        {
            // Put kill-switch rules back before Windows ends the session, so an
            // app that starts at the next logon can't leak before we're up.
            try
            {
                _vm!.StopBackgroundWork();
                Task.Run(() => _vm.ShutdownAsync()).Wait(TimeSpan.FromSeconds(8));
            }
            catch { }
        };

        AppPaths.EnsureCreated();
        _vm = new MainViewModel(Dispatcher);
        _window = new MainWindow(_vm, ExitApp);
        _vm.OwnerProvider = () => _window;
        _tray = new TrayIcon(_vm, ShowMain, ExitApp);

#if DEBUG
        int snap = Array.FindIndex(e.Args, a => a == "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            await RenderSnapshotsAsync(e.Args[snap + 1]);
            return;
        }
#endif

        if (!autostart) ShowMain();
        await _vm.InitializeAsync(autostart);
    }

    public void ShowMain()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async void ExitApp()
    {
        if (_exiting || _vm == null) return;
        if (_vm.IsTunnelActive)
        {
            var owner = _window is { IsVisible: true } ? _window : null;
            var text = "Sair do Mingal Tunnel desliga o túnel. Apps com kill-switch ficam sem internet até você abrir de novo.\n\nSair mesmo assim?";
            var r = owner != null
                ? MessageBox.Show(owner, text, "Mingal Tunnel", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(text, "Mingal Tunnel", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        _exiting = true;
        _window?.AllowClose();
        _vm.StopBackgroundWork();
        await _vm.ShutdownAsync();
        _tray?.Dispose();
        _instance?.ReleaseMutex();
        Shutdown();
    }

#if DEBUG
    /// <summary>Dev aid: renders every tab to PNG so layout can be checked without a desktop session.</summary>
    private async Task RenderSnapshotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var w = _window!;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -4000;
        w.Top = 0;
        w.ShowInTaskbar = false;
        w.Show();
        await _vm!.InitializeAsync(false, interactiveSetup: false);
        await Task.Delay(1500);
        for (int tab = 0; tab < 3; tab++)
        {
            _vm.SelectedTab = tab;
            await Task.Delay(700);
            w.UpdateLayout();
            var content = (FrameworkElement)w.Content;
            var dpi = VisualTreeHelper.GetDpi(w);
            var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            await using var fs = File.Create(Path.Combine(dir, $"tab{tab}.png"));
            enc.Save(fs);
        }
        _exiting = true;
        w.AllowClose();
        _vm.StopBackgroundWork();
        await _vm.ShutdownAsync();
        _tray?.Dispose();
        Shutdown();
    }
#endif
}
