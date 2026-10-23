using Avalonia.Interactivity;
using Quaykeep.Core.Crypto;
using Quaykeep.Core.Storage;

namespace Quaykeep.Views;

public partial class ChangePasswordWindow : DialogWindow
{
    private readonly VaultService _vault;

    public ChangePasswordWindow(VaultService vault)
    {
        InitializeComponent();
        _vault = vault;
        Opened += (_, _) => Current.Focus();
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        string cur = Current.Text ?? "", pwd = New.Text ?? "";
        if (pwd.Length < 8)
        {
            ShowError(L.Get("ChangePwd.TooShort"));
            return;
        }
        if (pwd != Confirm.Text)
        {
            ShowError(L.Get("Unlock.Mismatch"));
            return;
        }
        OkButton.IsEnabled = false;
        Busy.Opacity = 1;
        try
        {
            await Task.Run(() => _vault.ChangePassword(cur, pwd));
            MessageBox.Show(this, L.Get("ChangePwd.Done"), "Quaykeep", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            OkButton.IsEnabled = true;
            Busy.Opacity = 0;
            if (ex is WrongPasswordException) Current.Text = "";
            Current.Focus();
        }
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }
}
