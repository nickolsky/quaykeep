using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Quaykeep.Views;

public enum MessageBoxButton
{
    OK,
    OKCancel,
    YesNoCancel,
    YesNo,
}

public enum MessageBoxImage
{
    None,
    Error,
    Question,
    Warning,
    Information,
}

public enum MessageBoxResult
{
    None,
    OK,
    Cancel,
    Yes,
    No,
}

/// <summary>WPF's MessageBox on Avalonia: the same calls, a modal window that follows the app theme and language.</summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, text, caption, button, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var window = new MessageBoxWindow(text, caption, button, image, defaultResult) { Owner = owner };
        window.ShowDialog();
        return window.Result;
    }
}

internal sealed class MessageBoxWindow : DialogWindow
{
    public MessageBoxResult Result { get; private set; }

    public MessageBoxWindow(string text, string caption, MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Title = string.IsNullOrEmpty(caption) ? Core.AppPaths.ProductTitle : caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        MinWidth = 360;
        MaxWidth = 640;

        var buttons = button switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        // closing with the window's X or Escape: Cancel when there is one, else No (YesNo) or OK
        Result = buttons.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
            : buttons.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        var focused = defaultResult != MessageBoxResult.None && buttons.Contains(defaultResult) ? defaultResult : buttons[0];

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Button? first = null;
        foreach (var r in buttons)
        {
            var b = new Button
            {
                Content = L.Get(r switch
                {
                    MessageBoxResult.Yes => "Common.Yes",
                    MessageBoxResult.No => "Common.No",
                    MessageBoxResult.Cancel => "Common.Cancel",
                    _ => "Common.Ok",
                }),
                IsDefault = r == focused,
            };
            b.Classes.Add("dialog");
            if (r == focused) b.Classes.Add("accent");
            var result = r;
            b.Click += (_, _) =>
            {
                Result = result;
                DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
            };
            row.Children.Add(b);
            if (r == focused) first = b;
        }

        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        DockPanel.SetDock(row, Dock.Bottom);
        body.Children.Add(row);
        if (image != MessageBoxImage.None)
        {
            var (symbol, brush) = image switch
            {
                MessageBoxImage.Error => (Symbol.ErrorCircle, "SystemFillColorCriticalBrush"),
                MessageBoxImage.Warning => (Symbol.Warning, "SystemFillColorCautionBrush"),
                MessageBoxImage.Question => (Symbol.QuestionCircle, "AccentFillColorDefaultBrush"),
                _ => (Symbol.Info, "AccentFillColorDefaultBrush"),
            };
            var icon = new SymbolIcon { Symbol = symbol, FontSize = 32, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
            icon.SetResourceReference(SymbolIcon.ForegroundProperty, brush);
            DockPanel.SetDock(icon, Dock.Left);
            body.Children.Add(icon);
        }
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center });
        Content = body;
        Opened += (_, _) => first?.Focus();
    }
}
