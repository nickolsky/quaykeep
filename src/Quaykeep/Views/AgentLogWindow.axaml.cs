using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core.Mcp;
using Quaykeep.Core.Models;
using Quaykeep.Services;
using Quaykeep.Views.Controls;

namespace Quaykeep.Views;

/// <summary>
/// What AI agents did, as it happens (like tail -f): one server or all of them, the newest at the bottom — as a console
/// session (every call a prompt with its command and output, in colour) or as the log's own lines.
/// The files themselves are in data/agent-logs (rotated by size and age, see the settings).
/// </summary>
public partial class AgentLogWindow : Window
{
    private const int InitialLines = 500;
    private const int MaxChars = 2_000_000;

    private sealed record ServerChoice(Guid? Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The view chosen last (for the next window in this run of the app).</summary>
    private static bool _linesMode;

    private readonly AppHost _host;
    private readonly List<string> _paused = [];
    private readonly AgentConsole _console = new();
    private readonly List<ServerChoice> _choices;
    private int _shown;

    public AgentLogWindow(AppHost host, Guid? serverId)
    {
        InitializeComponent();
        _host = host;
        ShowView();
        Icon = IconFactory.Create(true);
        _choices = [new(null, L.Get("AgentLog.All"))];
        if (host.Vault.TryRead(d => d.Servers.OrderBy(s => s.Name).Select(s => (s.Id, s.Name, s.McpAccess)).ToList(), out var servers))
            _choices.AddRange(servers.Select(s => new ServerChoice(s.Id, s.McpAccess == McpAccess.Off ? $"{s.Name}  ({L.Get("Mcp.LevelOff")})" : s.Name)));
        ServerBox.ItemsSource = _choices;
        Select(serverId);
        host.Mcp.Log.Appended += OnAppended;
        Closed += (_, _) =>
        {
            host.Mcp.Log.Appended -= OnAppended;
            LogConsole.Dispose();
        };
        PauseButton.IsCheckedChanged += (_, _) =>
        {
            if (PauseButton.IsChecked != true) Resume();
        };
    }

    private Guid? Selected => (ServerBox.SelectedItem as ServerChoice)?.Id;

    /// <summary>Shows one server's log (null = all).</summary>
    public void Select(Guid? serverId) => ServerBox.SelectedItem = _choices.FirstOrDefault(c => c.Id == serverId) ?? _choices[0];

    private void OnServerChanged(object? sender, SelectionChangedEventArgs e) => Reload();

    private void OnViewClicked(object? sender, RoutedEventArgs e)
    {
        _linesMode = sender == LinesMode;
        ShowView();
    }

    private void ShowView()
    {
        ConsoleMode.IsChecked = !_linesMode;
        LinesMode.IsChecked = _linesMode;
        ConsoleHost.IsVisible = !_linesMode;
        Lines.IsVisible = _linesMode;
        if (_linesMode) Dispatcher.UIThread.Post(Lines.ScrollToEndOfText, DispatcherPriority.Background);
    }

    private void Reload()
    {
        var log = _host.Mcp.Log;
        var lines = Selected is { } id ? log.Tail(id, InitialLines) : log.TailAll(InitialLines);
        _paused.Clear();
        Lines.Text = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
        Dispatcher.UIThread.Post(Lines.ScrollToEndOfText, DispatcherPriority.Background);
        _console.Clear();
        LogConsole.Reset(Render(lines));
        _shown = lines.Count;
        ShowCount();
    }

    private void ShowCount() =>
        Status.Text = _shown == 0 ? L.Get("AgentLog.Empty") : L.F("AgentLog.Shown", _shown, _host.Mcp.Log.Folder);

    /// <summary>The console text for log lines; a line that is not an entry (edited by hand) shows as it is.</summary>
    private string Render(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(AgentLogEntry.TryParse(line) is { } entry ? _console.Render(entry) : line + "\r\n");
        return sb.ToString();
    }

    private void OnAppended(AgentLogEntry e) => Dispatcher.UIThread.Post(() =>
    {
        if (Selected is { } id && e.ServerId != id) return;
        var line = e.Format(); // what the file has: cut and on one line, so both views show the same as after a reload
        if (PauseButton.IsChecked == true)
        {
            _paused.Add(line);
            Status.Text = L.F("AgentLog.PausedCount", _paused.Count);
            return;
        }
        Append([line]);
    });

    private void Resume()
    {
        if (_paused.Count > 0) Append(_paused);
        _paused.Clear();
        ShowCount();
    }

    private void Append(IReadOnlyList<string> lines)
    {
        var atEnd = Lines.IsAtEnd();
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append('\n');
        Lines.AppendLog(sb.ToString(), MaxChars);
        if (atEnd) Lines.ScrollToEndOfText();
        LogConsole.Append(Render(lines)); // the terminal keeps its own scrollback limit
        _shown += lines.Count;
        ShowCount();
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var dir = _host.Mcp.Log.Folder;
        Directory.CreateDirectory(dir);
        UiExtensions.OpenWithShell(dir);
    }
}
