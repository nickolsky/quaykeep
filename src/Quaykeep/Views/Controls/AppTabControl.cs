using Avalonia.Controls;
using Avalonia.Input;

namespace Quaykeep.Views.Controls;

/// <summary>
/// TabControl whose Home / End only switch tabs while a tab header has the focus: the terminal and the editor get
/// keys they have not consumed yet back from the web view, and the stock control took them.
/// </summary>
public sealed class AppTabControl : TabControl
{
    // look exactly like a TabControl: the theme's styles are keyed by that type, not by this subclass
    protected override Type StyleKeyOverride => typeof(TabControl);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Home or Key.End && e.Source is not TabItem) return;
        base.OnKeyDown(e);
    }
}
