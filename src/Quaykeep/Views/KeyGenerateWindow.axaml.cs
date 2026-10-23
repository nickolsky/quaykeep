using Avalonia.Interactivity;
using Quaykeep.Core.Crypto;
using Quaykeep.Core.Models;

namespace Quaykeep.Views;

public partial class KeyGenerateWindow : DialogWindow
{
    public KeyEntry? Result { get; private set; }

    public KeyGenerateWindow() : this("")
    {
    }

    public KeyGenerateWindow(string defaultName)
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        CommentBox.Text = KeyService.SanitizeComment(
            string.IsNullOrWhiteSpace(defaultName) ? $"{Environment.UserName}@{Environment.MachineName}" : defaultName);
        Opened += (_, _) => NameBox.Focus();
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        var comment = KeyService.SanitizeComment((CommentBox.Text ?? "").Trim());
        var alg = TypeBox.SelectedIndex == 1 ? KeyAlgorithm.Rsa4096 : KeyAlgorithm.Ed25519;
        OkButton.IsEnabled = false;
        Busy.Opacity = 1;
        Result = await Task.Run(() => KeyService.Generate(name, comment, alg));
        DialogResult = true;
    }
}
