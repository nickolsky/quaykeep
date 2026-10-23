using Avalonia.Interactivity;
using Quaykeep.Core.Crypto;
using Quaykeep.Services;

namespace Quaykeep.Views;

public partial class UnlockWindow : DialogWindow
{
    private readonly AppHost _host;
    private readonly bool _create;

    public UnlockWindow(AppHost host, bool create)
    {
        InitializeComponent();
        _host = host;
        _create = create;
        Icon = IconFactory.Create(false);
        if (create)
        {
            Header.Text = L.Get("Unlock.CreateHeader");
            Subtitle.Text = L.Get("Unlock.CreateSubtitle");
            CreatePanel.IsVisible = true;
            OkButton.Content = L.Get("Unlock.Create");
        }
        Opened += (_, _) =>
        {
            Activate();
            Password.Focus();
        };
    }


    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        var pwd = Password.Text ?? "";
        if (pwd.Length == 0) return;
        if (_create)
        {
            if (pwd.Length < 8)
            {
                ShowError(L.Get("Unlock.TooShort"));
                return;
            }
            if (pwd != Confirm.Text)
            {
                ShowError(L.Get("Unlock.Mismatch"));
                return;
            }
        }

        SetBusy(true);
        var retry = false;
        try
        {
            if (_create)
            {
                await Task.Run(() => _host.Vault.Create(pwd));
                var autostart = AutostartBox.IsChecked == true;
                _host.SettingsStore.Settings.Autostart = autostart;
                _host.SettingsStore.Save();
                _host.TryApplyAutostart(autostart);
            }
            else
            {
                await Task.Run(() => _host.Vault.Unlock(pwd));
            }
            DialogResult = true;
        }
        catch (WrongPasswordException ex)
        {
            ShowError(ex.Message);
            Password.Text = "";
            retry = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            retry = true;
        }
        finally
        {
            SetBusy(false);
        }
        // only after SetBusy(false): a disabled box refuses focus
        if (retry) Password.Focus();
    }

    private void SetBusy(bool busy)
    {
        Busy.Opacity = busy ? 1 : 0;
        OkButton.IsEnabled = !busy;
        Password.IsEnabled = !busy;
        if (busy) Error.IsVisible = false;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }
}
