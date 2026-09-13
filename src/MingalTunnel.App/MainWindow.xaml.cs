using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using MingalTunnel.App.ViewModels;

namespace MingalTunnel.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Action _exit;
    private bool _allowClose;

    public MainWindow(MainViewModel vm, Action exit)
    {
        InitializeComponent();
        _vm = vm;
        _exit = exit;
        DataContext = vm;
        IsVisibleChanged += (_, _) => _vm.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
        StateChanged += (_, _) => _vm.WindowVisible = IsVisible && WindowState != WindowState.Minimized;
        ((INotifyCollectionChanged)vm.Log).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        e.Cancel = true;
        if (_vm.Settings.MinimizeToTray)
        {
            Hide();
            _vm.ShowTrayHintOnce();
        }
        else
        {
            _exit();
        }
    }
}
