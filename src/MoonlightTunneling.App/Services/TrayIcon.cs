using System.ComponentModel;
using System.Windows;
using MoonlightTunneling.App.ViewModels;
using Forms = System.Windows.Forms;

namespace MoonlightTunneling.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly MainViewModel _vm;

    public TrayIcon(MainViewModel vm, Action show, Action exit)
    {
        _vm = vm;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream), Visible = true, Text = "Moonlight Tunneling" };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Moonlight Tunneling", null, (_, _) => show());
        _toggle = new Forms.ToolStripMenuItem("Turn tunnel on", null, (_, _) => vm.ToggleTunnelCommand.Execute(null));
        menu.Items.Add(_toggle);
        menu.Items.Add("Discord Go Live boost", null, (_, _) => vm.BoostCommand.Execute(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };

        vm.PropertyChanged += OnVmChanged;
        vm.Notify += (title, msg) => _icon.ShowBalloonTip(6000, title, Trim(msg, 250), Forms.ToolTipIcon.Info);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StatusTitle) or nameof(MainViewModel.ToggleText))
        {
            _icon.Text = Trim($"Moonlight Tunneling · {_vm.StatusTitle}", 63);
            _toggle.Text = _vm.ToggleText;
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
