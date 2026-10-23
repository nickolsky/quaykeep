using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Quaykeep.Views.Controls;

/// <summary>
/// A read-only TextBox used as a growing log (script output, agent log lines): WPF had AppendText and the scroll
/// offsets on the TextBox itself; here they come from its template's ScrollViewer.
/// </summary>
public static class LogTextBox
{
    private static ScrollViewer? Scroller(TextBox box) => box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>The view shows the last line (or there is nothing to scroll yet).</summary>
    public static bool IsAtEnd(this TextBox box) =>
        Scroller(box) is not { } s || s.Offset.Y + s.Viewport.Height >= s.Extent.Height - 4;

    public static void ScrollToEndOfText(this TextBox box)
    {
        box.CaretIndex = box.Text?.Length ?? 0;
        Scroller(box)?.ScrollToEnd();
    }

    /// <summary>Adds text at the end, keeping at most <paramref name="maxChars"/> (the newer half when it grows past it).</summary>
    public static void AppendLog(this TextBox box, string text, int maxChars)
    {
        var all = (box.Text ?? "") + text;
        box.Text = all.Length > maxChars ? all[^(maxChars / 2)..] : all;
    }
}
