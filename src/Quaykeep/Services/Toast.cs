using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Quaykeep.Core;
using Quaykeep.Views;

namespace Quaykeep.Services;

/// <summary>
/// A notice in the corner of the screen (bottom right; top right on macOS, under the menu bar), stacked when there
/// are several, gone after a few seconds. Replaces the tray balloon, which only Windows has: this works the same on
/// every OS and needs no notification permission or registered app id.
/// </summary>
internal sealed class Toast : Window
{
    private const double Width0 = 360, Gap = 10;
    private static readonly List<Toast> Open = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    private Toast(string title, string text, bool warning, Action onClick)
    {
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = Width0;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        this.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var icon = new SymbolIcon { Symbol = warning ? Symbol.Warning : Symbol.Info, FontSize = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, warning ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush");
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 160 });
        var caption = new TextBlock { Text = AppPaths.ProductTitle, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6) };
        var panel = new DockPanel { Margin = new Thickness(16, 12, 16, 14) };
        DockPanel.SetDock(caption, Dock.Top);
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(caption);
        panel.Children.Add(icon);
        panel.Children.Add(body);
        var border = new Border { Child = panel, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = border;

        PointerPressed += (_, e) =>
        {
            var left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
            Close();
            if (left) onClick();
        };
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => Close();
        Opened += (_, _) =>
        {
            Place();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Open.Remove(this);
            foreach (var t in Open) t.Place();
        };
    }

    public static void Show(string title, string text, bool warning, Action onClick)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new Toast(title, text, warning, onClick);
            Open.Add(toast);
            toast.Show();
        });
    }

    public static void CloseAll()
    {
        foreach (var t in Open.ToList()) t.Close();
    }

    /// <summary>In the work area of the primary screen, after the toasts opened before it.</summary>
    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var w = (int)(Width0 * scale);
        var gap = (int)(Gap * scale);
        var index = Open.IndexOf(this);
        var offset = Open.Take(Math.Max(0, index)).Sum(t => (int)(t.Bounds.Height * scale) + gap);
        var h = (int)(Bounds.Height * scale);
        var x = area.Right - w - gap * 2;
        var y = OperatingSystem.IsMacOS() ? area.Y + gap + offset : area.Bottom - h - gap * 2 - offset;
        Position = new PixelPoint(x, y);
    }
}
