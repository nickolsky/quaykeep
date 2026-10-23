using Avalonia.Controls;

namespace Quaykeep.Views;

/// <summary>
/// A non-modal window used like WPF's: set <see cref="Owner"/>, then <see cref="Show()"/> (it stays above the owner and
/// closes with it). Avalonia's Owner is read-only and taken by Show(owner).
/// </summary>
public class ToolWindow : Window
{
    public ToolWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    public new Window? Owner { get; set; }

    public new void Show()
    {
        if (Owner is { IsVisible: true } owner) base.Show(owner);
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            base.Show();
        }
    }
}
