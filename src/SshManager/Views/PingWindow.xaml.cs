using System.ComponentModel;
using System.Net;
using System.Windows;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>ICMP ping of a server from this PC, once a second until stopped or closed ("Ping…" in the server menu).</summary>
public partial class PingWindow : Window
{
    private const int MaxLines = 500;

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly PingStats _stats = new();
    private IPAddress? _address;
    private CancellationTokenSource? _cts;

    public PingWindow(AppHost host, ServerEntry server)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Title = L.F("Ping.Title", server.Name);
        Header.Text = $"{server.Name}  ({server.Host})";
        JumpNote.Visibility = string.IsNullOrWhiteSpace(server.JumpHost) ? Visibility.Collapsed : Visibility.Visible;
        ShowStats();
        Loaded += (_, _) => Start();
        Closing += OnClosing;
    }

    public Guid ServerId => _server.Id;

    private async void Start()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ToggleButton.Content = L.Get("Ping.Stop");
        try
        {
            if (_address == null)
            {
                Append(L.F("Ping.Resolving", _server.Host));
                try
                {
                    _address = await PingService.ResolveAsync(_server.Host, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Append(L.F("Ping.ResolveFailed", _server.Host, ex.Message));
                    Stop();
                    return;
                }
                Header.Text = $"{_server.Name}  ({_server.Host} → {_address})";
            }
            await foreach (var r in PingService.PingAsync(_address, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), ct: cts.Token))
            {
                _stats.Add(r);
                Append(r.Ok
                    ? L.F("Ping.Reply", r.Seq, _address, r.RoundtripMs, r.Ttl?.ToString() ?? "?")
                    : L.F("Ping.Timeout", r.Seq, r.Error ?? r.Status.ToString()));
                ShowStats();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        ToggleButton.Content = L.Get("Ping.Start");
    }

    private void ShowStats()
    {
        Stats.Text = L.F("Ping.Stats", _stats.Sent, _stats.Received, _stats.LossPercent);
        Rtt.Text = _stats.Received > 0 ? L.F("Ping.StatsRtt", _stats.Min, _stats.Avg, _stats.Max) : "";
        var blocked = _stats.Sent >= 4 && _stats.Received == 0;
        Blocked.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (blocked) Blocked.Text = L.F("Ping.Blocked", SshState());
    }

    private string SshState()
    {
        var h = _host.Health.Get(_server.Id);
        return h.State switch
        {
            HealthState.Online => L.F("Health.OnlineTip", h.LatencyMs, h.Checked?.ToString("T", L.Culture)),
            HealthState.Offline => L.F("Health.OfflineTip", h.Checked?.ToString("T", L.Culture), h.Error).Replace('\n', ' '),
            HealthState.NotChecked => L.Get("Health.JumpHost"),
            HealthState.Disabled => L.Get("Health.Disabled"),
            _ => L.Get("Health.Unknown"),
        };
    }

    private void Append(string line)
    {
        if (Log.LineCount > MaxLines)
            Log.Text = Log.Text[(Log.Text.IndexOf('\n', Log.Text.Length / 2) + 1)..];
        Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        Log.ScrollToEnd();
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_cts != null) Stop();
        else Start();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = $"{Header.Text}\n{Log.Text}{Stats.Text}\n{Rtt.Text}".TrimEnd();
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // the clipboard is held by another program
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e) => _cts?.Cancel();
}
