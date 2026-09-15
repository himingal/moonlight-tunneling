using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MingalTunnel.App.ViewModels;

namespace MingalTunnel.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Action _exit;
    private bool _allowClose, _scrollQueued;

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
            // Never touch the ListBox from inside the collection's own change
            // notification: the ListBox may not have seen the change yet, and
            // reading its Items there throws "ItemsControl is inconsistent with
            // its items source" - which, logged, re-triggered itself forever.
            if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;
            _scrollQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                _scrollQueued = false;
                if (vm.Log.Count > 0) LogList.ScrollIntoView(vm.Log[^1]);
            });
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
