using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using MingalTunnel.App.Services;
using MingalTunnel.App.ViewModels;
using MingalTunnel.Core;
using MingalTunnel.Platform;

namespace MingalTunnel.App;

public sealed class Candidate : ObservableObject
{
    private bool _selected;
    private ImageSource? _icon;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? Note { get; init; }
    public bool HasNote => Note != null;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public partial class AddAppWindow : Window
{
    private readonly ObservableCollection<Candidate> _network = [];
    private readonly ObservableCollection<Candidate> _startMenu = [];

    public List<TunneledApp> Result { get; } = [];

    public AddAppWindow()
    {
        InitializeComponent();
        NetworkList.ItemsSource = _network;
        StartMenuList.ItemsSource = _startMenu;
        Loaded += async (_, _) => await LoadAsync();
    }

    private static string? NoteFor(string path) =>
        AppCatalog.IsSharedWebView(path) ? "Shared by several apps (Teams, WhatsApp, Outlook…): they all go together." : null;

    private async Task LoadAsync()
    {
        var self = Environment.ProcessPath ?? "";
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var net = await Task.Run(() =>
        {
            var pids = ConnectionTables.PidsWithSockets();
            return pids.Select(ProcessPaths.GetPath)
                .Where(p => p != null && !p.StartsWith(windir, StringComparison.OrdinalIgnoreCase) &&
                            !p.Equals(self, StringComparison.OrdinalIgnoreCase) &&
                            !System.IO.Path.GetFileName(p).Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new Candidate { Name = AppCatalog.FriendlyName(p), Path = p, Note = NoteFor(p) })
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        });
        foreach (var c in net) _network.Add(c);
        NetworkLoading.Visibility = _network.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NetworkLoading.Text = "No program has an open connection right now.";

        var menu = await Task.Run(() => ShellLinks.ScanStartMenu()
            .Where(s => !s.TargetPath.StartsWith(windir, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(s => new Candidate { Name = s.Name, Path = s.TargetPath, Note = NoteFor(s.TargetPath) })
            .ToList());
        foreach (var c in menu) _startMenu.Add(c);
        StartMenuLoading.Visibility = _startMenu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartMenuLoading.Text = "Nothing found in the Start menu.";

        // Icons last, off the UI thread; frozen bitmaps can cross threads.
        var all = _network.Concat(_startMenu).ToList();
        await Task.Run(() =>
        {
            foreach (var c in all)
            {
                var icon = IconCache.Get(c.Path);
                Dispatcher.BeginInvoke(() => c.Icon = icon);
            }
        });
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = Filter.Text.Trim();
        FilterHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Predicate<object>? pred = text.Length == 0 ? null : o =>
            o is Candidate c && (c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || c.Path.Contains(text, StringComparison.OrdinalIgnoreCase));
        CollectionViewSource.GetDefaultView(_network).Filter = pred;
        CollectionViewSource.GetDefaultView(_startMenu).Filter = pred;
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Pick the executable", Filter = "Programs (*.exe)|*.exe", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) Result.Add(AppCatalog.FromExecutable(f));
        DialogResult = true;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the folder (every .exe inside it goes through the tunnel)" };
        if (dlg.ShowDialog(this) != true) return;
        var root = System.IO.Path.GetPathRoot(dlg.FolderName);
        if (string.Equals(dlg.FolderName.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase) ||
            dlg.FolderName.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Pick the program or game folder, not a drive root or the Windows folder.", "Moonlight Tunneling");
            return;
        }
        Result.Add(AppCatalog.FromFolder(dlg.FolderName));
        DialogResult = true;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var chosen = _network.Concat(_startMenu).Where(c => c.IsSelected)
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one app in the list, or use Pick .exe / folder.", "Moonlight Tunneling");
            return;
        }
        foreach (var c in chosen) Result.Add(AppCatalog.FromExecutable(c.Path, c.Name));
        DialogResult = true;
    }
}
