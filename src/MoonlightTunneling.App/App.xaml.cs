using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoonlightTunneling.App.Services;
using MoonlightTunneling.App.ViewModels;
using MoonlightTunneling.Core;

namespace MoonlightTunneling.App;

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

        bool devRender = false;
#if DEBUG
        // Screenshot/stress renders and --dev runs work beside a real installed copy.
        devRender = e.Args.Contains("--snapshot") || e.Args.Contains("--stress-log") || e.Args.Contains("--dev");
#endif
        _instance = new Mutex(true, devRender ? "MoonlightTunneling.DevRender." + Environment.ProcessId : "MoonlightTunneling.SingleInstance", out bool first);
        if (!first)
        {
            // Second launch just brings the running window forward.
            try { EventWaitHandle.OpenExisting("MoonlightTunneling.Activate").Set(); } catch { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, devRender ? "MoonlightTunneling.DevActivate." + Environment.ProcessId : "MoonlightTunneling.Activate");
        new Thread(() =>
        {
            while (_activate.WaitOne()) Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, a) =>
        {
            // Full detail (type, inner exception, where) so the log is actually
            // useful; AppLog collapses repeats so a looping error can't flood it.
            var ex = a.Exception;
            var inner = ex.InnerException != null ? $" ← {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
            var where = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("MoonlightTunneling"))?.Trim() ?? "";
            AppLog.Error($"Unexpected error: {ex.GetType().Name}: {ex.Message}{inner} {where}".Trim());
            a.Handled = true;
        };
        // Fire-and-forget work (kill-switch sync, live refresh) must never fail silently.
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("Background task failed: " + a.Exception.GetBaseException().Message);
            a.SetObserved();
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

        var moved = RenameMigration.MoveDataFolder();
        AppPaths.EnsureCreated();
        if (moved != null) AppLog.Info(moved);
        _vm = new MainViewModel(Dispatcher);
        _window = new MainWindow(_vm, ExitApp);
        _vm.OwnerProvider = () => _window;
        _tray = new TrayIcon(_vm, ShowMain, ExitApp);

#if DEBUG
        if (e.Args.Contains("--stress-log"))
        {
            await StressLogAsync();
            return;
        }
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
            var text = "Exiting Moonlight Tunneling turns the tunnel off. Apps with the kill-switch stay offline until you open it again.\n\nExit anyway?";
            var r = owner != null
                ? MessageBox.Show(owner, text, "Moonlight Tunneling", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(text, "Moonlight Tunneling", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
        w.Width = 1120;
        w.Height = 800;
        w.Show();
        await _vm!.InitializeAsync(false, interactiveSetup: false);
        await Task.Delay(2500);
        _vm.PrepareForScreenshots();
        string[] names = ["apps", "profiles", "settings"];
        for (int tab = 0; tab < 3; tab++)
        {
            _vm.SelectedTab = tab;
            await Task.Delay(700);
            Render(w, Path.Combine(dir, $"{names[tab]}.png"));
        }

        // Well-known apps only, instead of everything that happens to run on the dev box.
        var demo = AppCatalog.DetectInstalledCurated().Select(AppCatalog.IconSource).OfType<string>().ToList();
        foreach (var extra in new[] { @"C:\Program Files (x86)\Steam\steam.exe", @"C:\Riot Games\Riot Client\RiotClientServices.exe" })
            if (File.Exists(extra)) demo.Add(extra);
        var add = new AddAppWindow { Owner = w, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowInTaskbar = false, DemoPaths = demo };
        add.Show();
        await Task.Delay(2500);
        Render(add, Path.Combine(dir, "add-app.png"));
        add.Close();
        _exiting = true;
        w.AllowClose();
        _vm.StopBackgroundWork();
        await _vm.ShutdownAsync();
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// Regression check for the log-panel crash loop: floods the log from
    /// several threads with the window on screen, then reports whether any
    /// UI exception was raised. Writes stress-result.txt in the data dir.
    /// </summary>
    private async Task StressLogAsync()
    {
        var w = _window!;
        w.Show();
        await _vm!.InitializeAsync(false, interactiveSetup: false);
        int errors = 0;
        AppLog.Added += en => { if (en.Message.StartsWith("Unexpected error")) Interlocked.Increment(ref errors); };
        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 1500; i++) AppLog.Info($"stress {t}-{i}");
        })));
        await Task.Delay(4000);
        var result = $"errors={errors} logLines={_vm.Log.Count}";
        File.WriteAllText(Path.Combine(AppPaths.DataDir, "stress-result.txt"), result);
        _exiting = true;
        w.AllowClose();
        _vm.StopBackgroundWork();
        await _vm.ShutdownAsync();
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>Renders a window's content at 2x for crisp README images.</summary>
    private static void Render(Window w, string file)
    {
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        const double scale = 2;
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var ctx = bg.RenderOpen())
            ctx.DrawRectangle(w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
#endif
}
