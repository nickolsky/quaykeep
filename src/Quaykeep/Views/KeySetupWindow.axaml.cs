using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

public partial class KeySetupWindow : DialogWindow
{
    private sealed record KeyChoice(Guid? Id, string Name);

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private bool _running;

    public KeySetupWindow(AppHost host, ServerEntry server)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Header.Text = $"{server.Name}  ({server.Display})";
        var choices = new List<KeyChoice> { new(null, L.Get("KeySetup.GenerateNew")) };
        choices.AddRange(host.Vault.Data.Keys.OrderBy(k => k.Name).Select(k => new KeyChoice(k.Id, L.Get("KeySetup.Existing") + " " + k.Name)));
        KeyBox.ItemsSource = choices;
        KeyBox.SelectedIndex = 0;
        if (!server.SshLogin)
        {
            // an SSM shell: the key goes in through AWS, for the Linux user the SSH login will use
            HintText.Text = L.Get("KeySetup.SsmHint");
            UserPanel.IsVisible = true;
            UserBox.Text = server.Username is { Length: > 0 } u && u != "root" ? u : "ssm-user";
            PasswordOffPanel.IsVisible = false;
        }
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_running) e.Cancel = true;
    }

    private void Append(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            Log.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
            Log.CaretIndex = Log.Text.Length;
        });

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        var keyId = (KeyBox.SelectedItem as KeyChoice)?.Id;
        var disablePassword = DisablePasswordBox.IsChecked == true;
        var user = UserBox.Text ?? "";
        _running = true;
        StartButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        KeyBox.IsEnabled = false;
        DisablePasswordBox.IsEnabled = false;
        UserBox.IsEnabled = false;
        Busy.Opacity = 1;
        try
        {
            var server = _server;
            var key = await Task.Run(() => server.SshLogin
                ? _host.KeySetup.SetupKeyAuth(server, keyId, Append, disablePassword)
                : _host.KeySetup.SetupKeyAuthViaSsm(server, keyId, user, Append));
            Append(L.F("KeySetup.Done", key.Name, key.Fingerprint));
            StartButton.Content = L.Get("KeySetup.DoneButton");
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
            StartButton.IsEnabled = true;
            KeyBox.IsEnabled = true;
            DisablePasswordBox.IsEnabled = true;
            UserBox.IsEnabled = true;
        }
        finally
        {
            _running = false;
            CloseButton.IsEnabled = true;
            Busy.Opacity = 0;
        }
    }
}
