using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MingalTunnel.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? canRun = null)
        : this(_ => run(), canRun == null ? null : _ => canRun()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => run(parameter);
}

/// <summary>Async command that ignores re-entry while running (double clicks on "Ligar").</summary>
public sealed class AsyncCommand(Func<object?, Task> run, Func<object?, bool>? canRun = null) : ICommand
{
    private bool _running;

    public AsyncCommand(Func<Task> run, Func<bool>? canRun = null)
        : this(_ => run(), canRun == null ? null : _ => canRun()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canRun?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await run(parameter);
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("Erro: " + ex.Message);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
