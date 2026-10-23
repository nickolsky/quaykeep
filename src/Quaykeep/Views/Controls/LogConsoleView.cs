using System.Text;
using System.Text.Json;
using Avalonia.Controls;

namespace Quaykeep.Views.Controls;

/// <summary>
/// Read-only terminal (xterm.js in the system web view, Assets\terminal\log.html) that shows ANSI text written by the
/// app: the agent log as a console session. Text written before the page is ready is kept and sent once it is.
/// </summary>
public sealed class LogConsoleView : Border, IDisposable
{
    private readonly WebPage _web = new("terminal", "log.html", contextMenu: false);
    private readonly StringBuilder _early = new();
    private bool _ready;
    private bool _disposed;

    public LogConsoleView()
    {
        Child = _web;
        _web.MessageReceived += OnMessage;
        ActualThemeVariantChanged += (_, _) => SendTheme();
    }

    /// <summary>Replaces everything shown and scrolls to the end.</summary>
    public void Reset(string ansi)
    {
        _early.Clear().Append(ansi);
        if (_ready) Post(new { t = "reset", d = ansi });
    }

    /// <summary>Adds text at the end (the view follows it when it was at the end).</summary>
    public void Append(string ansi)
    {
        if (_ready) Post(new { t = "out", d = ansi });
        else _early.Append(ansi);
    }

    public void Focus() => _web.FocusPage();

    private void OnMessage(JsonElement m)
    {
        string S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (S("t"))
        {
            case "ready":
                _ready = true;
                SendTheme();
                Post(new { t = "labels", copy = L.Get("Term.MenuCopy"), selectAll = L.Get("Term.MenuSelectAll") });
                Post(new { t = "reset", d = _early.ToString() });
                _early.Clear();
                break;
            case "copy":
                if (S("d") is { Length: > 0 } copied) Clipboard.SetText(copied);
                break;
            case "link":
                if (Uri.TryCreate(S("url"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    UiExtensions.OpenWithShell(uri.AbsoluteUri);
                break;
        }
    }

    /// <summary>Dark or light palette on the app's background colour.</summary>
    public void SendTheme()
    {
        var (dark, bg) = WebPage.PageTheme(this);
        Post(new { t = "theme", dark, bg });
    }

    private void Post(object message)
    {
        if (_ready && !_disposed) _web.Post(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _web.Dispose();
    }
}
