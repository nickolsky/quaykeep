using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Quaykeep.Views;

/// <summary>
/// Waits for a task on the UI thread without blocking it: a nested dispatcher frame keeps windows painting and
/// answering until the task ends. Lets the views keep WPF's synchronous ShowDialog / MessageBox.Show flow.
/// </summary>
public static class UiWait
{
    public static T Run<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    public static void Run(Task task) => Run(task.ContinueWith(t =>
    {
        t.GetAwaiter().GetResult();
        return true;
    }, TaskScheduler.Default));

    /// <summary>The window the user works in: the active one, else the main window, else any visible one.</summary>
    public static Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive)
               ?? desktop.Windows.FirstOrDefault(w => w is MainWindow { IsVisible: true })
               ?? desktop.Windows.LastOrDefault(w => w.IsVisible);
    }
}

/// <summary>
/// A window used like WPF's dialogs: set <see cref="Owner"/>, call <see cref="ShowDialog()"/>, read the result;
/// setting <see cref="DialogResult"/> closes it. A button with IsCancel (or Escape) closes it with false.
/// </summary>
public class DialogWindow : Window
{
    private bool? _result;
    private bool _closing;

    public DialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AddHandler(Button.ClickEvent, OnAnyClick, RoutingStrategies.Bubble);
    }

    /// <summary>The window this dialog is modal to; null = the active window (or none: a window of its own).</summary>
    public new Window? Owner { get; set; }

    public bool? DialogResult
    {
        get => _result;
        set
        {
            _result = value;
            if (!_closing) Close();
        }
    }

    /// <summary>Shows the dialog modally and returns once it is closed.</summary>
    public bool? ShowDialog()
    {
        var owner = Owner ?? UiWait.ActiveWindow();
        if (owner == this) owner = null;
        var closed = new TaskCompletionSource<bool>();
        Closed += (_, _) => closed.TrySetResult(true);
        if (owner is { IsVisible: true })
        {
            // an owned window plus a disabled owner rather than Avalonia's ShowDialog, which left the dialog blank
            // when its modal loop ran inside our nested frame
            owner.IsEnabled = false;
            Closed += (_, _) =>
            {
                owner.IsEnabled = true;
                owner.Activate();
            };
            Show(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Show();
            Activate();
        }
        UiWait.Run(closed.Task);
        return _result;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) _closing = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void OnAnyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { IsCancel: true }) DialogResult = false;
    }
}
