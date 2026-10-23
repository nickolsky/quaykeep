using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace Quaykeep.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        RelayCommand.InvalidateAll();
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// WPF re-asked every command's CanExecute after any input (CommandManager.RequerySuggested); Avalonia has no such
/// thing. Here commands re-ask after a view model property changes and after mouse or key input
/// (<see cref="InvalidateAll"/>, coalesced into one pass per dispatcher turn).
/// </summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private static event EventHandler? Requery;
    private static int _queued;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            if (canExecute != null) Requery += value;
        }
        remove => Requery -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Every command with a CanExecute asks it again (soon, on the UI thread).</summary>
    public static void InvalidateAll()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            Requery?.Invoke(null, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }
}
