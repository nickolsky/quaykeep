using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quaykeep.Views;

public partial class InputDialog : DialogWindow
{
    private InputDialog(string title, string prompt, string value, bool password)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        if (password) TextValue.PasswordChar = '•';
        TextValue.Text = value;
        Opened += (_, _) =>
        {
            TextValue.Focus();
            if (!password) TextValue.SelectAll();
        };
    }

    public string Value => TextValue.Text ?? "";

    public static string? Ask(Window? owner, string title, string prompt, string value = "", bool password = false)
    {
        var dlg = new InputDialog(title, prompt, value, password) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => DialogResult = true;
}
