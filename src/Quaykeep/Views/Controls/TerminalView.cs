using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Terminal;

namespace Quaykeep.Views.Controls;

/// <summary>
/// One built-in terminal: xterm.js in the system web view (Assets\terminal, see <see cref="WebPage"/>) connected to a
/// <see cref="TerminalSession"/>. Output is batched every few milliseconds so a flood (cat of a big file) does not stall
/// the UI thread.
/// </summary>
public sealed class TerminalView : Border, IDisposable
{
    private readonly WebPage _web = new("terminal", "terminal.html", contextMenu: false);
    private readonly Func<TerminalSession> _createSession;
    private readonly string? _command;
    private readonly bool _autoSuggest;
    private TerminalAssist? _assist;
    private CancellationTokenSource? _completing;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;
    private TerminalSession? _session;
    private bool _ready;
    private bool _connecting;
    private bool _disposed;
    private int _cols = 120, _rows = 30;

    /// <param name="createSession">New session for this tab (also used to reconnect).</param>
    /// <param name="command">Typed into the shell after connecting (quick actions).</param>
    /// <param name="autoSuggest">Show the suggestion list while typing (otherwise on Ctrl+Space only).</param>
    public TerminalView(Func<TerminalSession> createSession, string? command, bool autoSuggest = true)
    {
        _createSession = createSession;
        _command = command;
        _autoSuggest = autoSuggest;
        Child = _web;
        _flush = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        _flush.Tick += (_, _) => Flush();
        _web.MessageReceived += OnMessage;
        _web.Failed += _ => SetState(TerminalState.Closed);
        ActualThemeVariantChanged += (_, _) => SendTheme();
    }

    /// <summary>Connected, connecting, closed (for the tab's dot).</summary>
    public event Action<TerminalState>? StateChanged;
    /// <summary>Title set by the remote shell (OSC 0/2).</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+Shift+W, Ctrl+Shift+T from inside the terminal.</summary>
    public event Action<string>? KeyCommand;
    /// <summary>"edit FILE" in the shell: an absolute remote path.</summary>
    public event Action<string>? EditRequested;

    /// <summary>The shell's working directory (with the shell integration), null when unknown.</summary>
    public string? Cwd => _assist?.Cwd;

    public TerminalState State { get; private set; } = TerminalState.Connecting;

    public void Focus()
    {
        _web.FocusPage();
        Post(new { t = "focus" });
    }

    // ---------- page → app ----------

    private void OnMessage(JsonElement m)
    {
        string S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
        switch (S("t"))
        {
            case "ready":
                _ready = true;
                (_cols, _rows) = (Math.Max(10, I("cols")), Math.Max(2, I("rows")));
                SendTheme();
                Post(new
                {
                    t = "config", auto = _autoSuggest, hint = L.Get("Term.SuggestHint"),
                    menu = new
                    {
                        copy = L.Get("Term.MenuCopy"), paste = L.Get("Term.MenuPaste"), selectAll = L.Get("Term.MenuSelectAll"),
                        clear = L.Get("Term.MenuClear"), files = L.Get("Tab.Files"), dup = L.Get("Tab.NewTerminal"),
                    },
                });
                Connect();
                break;
            case "in":
                _session?.Send(S("d"));
                break;
            case "size":
                (_cols, _rows) = (Math.Max(10, I("cols")), Math.Max(2, I("rows")));
                _session?.Resize(_cols, _rows);
                break;
            case "paste":
                var text = Clipboard.GetText();
                if (text.Length > 0) Post(new { t = "paste", d = text.Replace("\r\n", "\n") });
                break;
            case "copy":
                if (S("d") is { Length: > 0 } copied) Clipboard.SetText(copied);
                break;
            case "title":
                TitleChanged?.Invoke(S("d"));
                break;
            case "key":
                KeyCommand?.Invoke(S("k"));
                break;
            case "link":
                if (Uri.TryCreate(S("url"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    UiExtensions.OpenWithShell(uri.AbsoluteUri);
                break;
            case "reconnect":
                if (State == TerminalState.Closed) Connect();
                break;
            case "complete":
                Complete(I("id"), S("line"), I("cursor"), m.TryGetProperty("explicit", out var ex) && ex.ValueKind == JsonValueKind.True);
                break;
            case "cmd":
                _assist?.AddCommand(S("d"));
                break;
            case "cwd":
                if (_assist != null)
                {
                    var first = _assist.Cwd == null;
                    _assist.Cwd = S("d");
                    if (first) _assist.Start(); // the integration works: load the server's commands and history
                }
                break;
            case "edit":
                if (S("d") is { Length: > 1 } path && path.StartsWith('/')) EditRequested?.Invoke(path);
                break;
        }
    }

    // ---------- session ----------

    private async void Connect()
    {
        if (_connecting || _disposed) return;
        _connecting = true;
        SetState(TerminalState.Connecting);
        _session?.Dispose();
        var session = _createSession();
        session.Output += OnOutput;
        session.Closed += OnClosed;
        _session = session;
        _assist = new TerminalAssist(session.Server, session.Run);
        Post(new { t = "banner", text = L.F("Term.Connecting", session.Server.Name, session.Server.Display) });
        try
        {
            var (cols, rows) = (_cols, _rows);
            await Task.Run(() => session.Open(cols, rows, _command));
            if (_disposed) return;
            Post(new { t = "opened" });
            SetState(TerminalState.Connected);
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Post(new { t = "banner", text = "" });
            Post(new { t = "closed", text = L.F("Term.ConnectFailed", ex.Message) });
            SetState(TerminalState.Closed);
        }
        finally
        {
            _connecting = false;
        }
    }

    private async void Complete(int id, string line, int cursor, bool explicitRequest)
    {
        var assist = _assist;
        if (assist == null) return;
        _completing?.Cancel();
        var cts = _completing = new CancellationTokenSource();
        try
        {
            var r = await assist.CompleteAsync(line, cursor, explicitRequest, cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            Post(new
            {
                t = "completions", id, ghost = r.Ghost,
                items = r.Items.Select(i => new { l = i.Label, k = i.Kind, d = i.Detail, del = i.Delete, ins = i.Insert }),
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private void OnOutput(string text)
    {
        lock (_pending) _pending.Append(text);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_flush.IsEnabled) _flush.Start();
        });
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            if (_pending.Length == 0)
            {
                _flush.Stop();
                return;
            }
            text = _pending.ToString();
            _pending.Clear();
        }
        Post(new { t = "out", d = text });
    }

    private void OnClosed(string? error) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Flush();
        Post(new { t = "closed", text = error == null ? L.Get("Term.Ended") : L.F("Term.Lost", error) });
        SetState(TerminalState.Closed, error == null);
    });

    private void SetState(TerminalState state, bool endedByUser = false)
    {
        State = state;
        EndedNormally = endedByUser;
        StateChanged?.Invoke(state);
    }

    /// <summary>The last close was the shell exiting (exit / logout), not a lost connection.</summary>
    public bool EndedNormally { get; private set; }

    // ---------- theme ----------

    /// <summary>Follows the app theme: dark or light palette on the app's background colour.</summary>
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
        _flush.Stop();
        _completing?.Cancel();
        _session?.Dispose();
        _session = null;
        _web.Dispose();
    }
}

public enum TerminalState
{
    Connecting,
    Connected,
    Closed,
}
