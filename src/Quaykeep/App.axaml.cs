using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Quaykeep.Services;
using Quaykeep.Views;

namespace Quaykeep;

public partial class App : Application
{
    /// <summary>Started by the autostart entry (--tray): stay in the tray, locked.</summary>
    public static bool StartInTray { get; set; }

    public static AppHost Host { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            MessageBox.Show(e.Exception.Message, L.Get("App.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        };
        desktop.Exit += (_, _) => Host?.Dispose();
        // any input in our windows: commands re-check CanExecute (WPF did that itself) and the idle clock restarts
        // where the OS gives no idle time (Wayland outside GNOME)
        void OnInput()
        {
            Mvvm.RelayCommand.InvalidateAll();
            Services.Platform.IdleTime.NoteAppInput();
        }
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>((_, _) => OnInput(), handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>((_, _) => OnInput(), handledEventsToo: true);
        Host = new AppHost();
        // after the loop is running: the first window (unlock) is modal and runs a nested loop
        Dispatcher.UIThread.Post(() => Host.Start(StartInTray));
    }
}
