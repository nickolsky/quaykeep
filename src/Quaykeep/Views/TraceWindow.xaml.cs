using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Geo;
using Quaykeep.Core.Models;
using Quaykeep.Core.Monitoring;
using Quaykeep.Core.Ssh;
using Quaykeep.Mvvm;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// Traceroute from this PC to the server (like tracert), or from the server to this PC or anywhere (over SSH), with each
/// hop's network and location and a reading of the route: providers, countries, where the delay is added.
/// </summary>
public partial class TraceWindow : Window
{
    private sealed class HopRow(TraceHop hop) : ObservableObject
    {
        private string? _hostName;
        private string _network = "";
        private string _location = "";
        private string _note = "";

        public TraceHop Hop { get; } = hop;
        public int Number => Hop.Number;
        public string Address => Hop.Address ?? "*";
        public string Times => Hop.Silent ? "* * *" : string.Join(" · ", Hop.Rtts.Select(r => r is { } v ? Math.Round(v).ToString() : "*")) + " ms";
        public string? HostName { get => _hostName; set => Set(ref _hostName, value); }
        public string Network { get => _network; set => Set(ref _network, value); }
        public string Location { get => _location; set => Set(ref _location, value); }
        public string Note { get => _note; set => Set(ref _note, value); }
    }

    /// <param name="Host">null = this PC (the address the server sees it with).</param>
    private sealed record TargetChoice(string Label, string? Host)
    {
        public override string ToString() => Label;
    }

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly ObservableCollection<HopRow> _rows = [];
    private CancellationTokenSource? _cts;

    public TraceWindow(AppHost host, ServerEntry server)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Title = L.F("Trace.Title", server.Name);
        Header.Text = $"{server.Name}  ({server.Host})";
        FromServer.Content = L.F("Trace.FromServer", server.Name);
        HopsGrid.ItemsSource = _rows;
        var choices = new List<TargetChoice> { new(L.Get("Trace.ThisPc"), null) };
        if (host.Vault.TryRead(d => d.Servers.Where(s => s.Id != server.Id).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => new TargetChoice($"{s.Name}  ({s.Host})", AddressBook.AddressToward(server, s) ?? s.Host)).ToList(), out var others))
            choices.AddRange(others);
        TargetBox.ItemsSource = choices;
        TargetBox.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(server.JumpHost))
        {
            FromServer.IsEnabled = false;
            FromServer.ToolTip = L.Get("Inventory.JumpHost");
        }
        PrivacyText.Text = host.Geo.Enabled ? L.F("Trace.Privacy", host.Geo.ProviderName) : L.Get("Trace.GeoOff");
        Loaded += (_, _) => OnStart(this, new RoutedEventArgs());
        Closing += (_, _) => _cts?.Cancel();
    }

    public Guid ServerId => _server.Id;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (TargetBox != null) TargetBox.IsEnabled = FromServer.IsChecked == true;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        StateText.Text = "";
        _rows.Clear();
        NotesList.ItemsSource = null;
        StartButton.Content = L.Get("Trace.Stop");
        Busy.Visibility = Visibility.Visible;
        FromPc.IsEnabled = TargetBox.IsEnabled = false;
        FromServer.IsEnabled = false;
        try
        {
            var hops = FromServer.IsChecked == true ? await FromServerAsync(cts.Token) : await FromPcAsync(cts.Token);
            if (hops == null || cts.IsCancellationRequested) return;
            await InterpretAsync(hops, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StateText.Text = L.Get("Common.Error") + " " + ex.Message;
        }
        finally
        {
            if (cts.IsCancellationRequested && StateText.Text.Length == 0) StateText.Text = L.Get("Trace.Stopped");
            _cts = null;
            StartButton.Content = L.Get("Trace.Start");
            Busy.Visibility = Visibility.Hidden;
            FromPc.IsEnabled = true;
            FromServer.IsEnabled = string.IsNullOrWhiteSpace(_server.JumpHost);
            TargetBox.IsEnabled = FromServer.IsChecked == true;
        }
    }

    private async Task<List<TraceHop>?> FromPcAsync(CancellationToken ct)
    {
        StateText.Text = L.F("Ping.Resolving", _server.Host);
        IPAddress target;
        try
        {
            target = await PingService.ResolveAsync(_server.Host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StateText.Text = L.F("Ping.ResolveFailed", _server.Host, ex.Message);
            return null;
        }
        Header.Text = $"{_server.Name}  ({L.Get("Trace.ThisPc")} → {_server.Host}" + (target.ToString() != _server.Host ? $" → {target})" : ")");
        StateText.Text = L.Get("Trace.Running");
        var hops = new List<TraceHop>();
        await foreach (var hop in TraceRoute.RunAsync(target, TimeSpan.FromSeconds(2), ct: ct))
        {
            hops.Add(hop);
            AddRow(hop);
        }
        StateText.Text = "";
        return hops;
    }

    private async Task<List<TraceHop>?> FromServerAsync(CancellationToken ct)
    {
        var choice = TargetBox.SelectedItem as TargetChoice;
        var typed = TargetBox.Text.Trim();
        var server = _server;
        StateText.Text = L.F("Fwd.Connecting", server.Display);
        var (target, output) = await Task.Run(() =>
        {
            using var client = _host.Ssh.Connect(server);
            using var abort = ct.Register(() =>
            {
                try
                {
                    client.Disconnect();
                }
                catch (Exception)
                {
                    // already gone
                }
            });
            string target;
            if (choice != null && choice.Label == typed || choice != null && typed.Length == 0) target = choice.Host ?? ThisPc(client);
            else target = typed;
            Dispatcher.Invoke(() =>
            {
                Header.Text = $"{server.Name} → {target}";
                StateText.Text = L.Get("Trace.RunningOnServer");
            });
            var script = TraceRoute.RemoteScript(target);
            var r = RemoteShell.Run(client, server, script, elevated: true, TimeSpan.FromMinutes(3));
            if (RemoteShell.SudoFailed(r)) r = RemoteShell.Run(client, server, script, elevated: false, TimeSpan.FromMinutes(3));
            return (target, r.Output);
        }, ct);
        ct.ThrowIfCancellationRequested();
        var hops = TraceRoute.ParseRemote(output, target);
        if (hops == null)
        {
            StateText.Text = L.Get("Trace.NoTool");
            return null;
        }
        foreach (var hop in hops) AddRow(hop);
        StateText.Text = "";
        return hops;
    }

    /// <summary>This PC as the server sees it (after NAT: the public address of this network).</summary>
    private static string ThisPc(Renci.SshNet.SshClient client)
    {
        var text = client.RunCommand("echo \"${SSH_CLIENT%% *}\"").Result.Trim();
        return IPAddress.TryParse(text, out var ip) ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
            : throw new InvalidOperationException(L.Get("Trace.NoClientIp"));
    }

    private void AddRow(TraceHop hop)
    {
        var row = new HopRow(hop);
        _rows.Add(row);
        if (hop.Address is { } a && IPAddress.TryParse(a, out var ip)) _ = ReverseAsync(row, ip);
    }

    private static async Task ReverseAsync(HopRow row, IPAddress ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(3));
            if (entry.HostName != ip.ToString()) row.HostName = entry.HostName;
        }
        catch (Exception)
        {
            // no PTR record: most routers have one, some do not
        }
    }

    /// <summary>Networks and places of the hops (one batch lookup), then the reading of the route.</summary>
    private async Task InterpretAsync(List<TraceHop> hops, CancellationToken ct)
    {
        var geo = new Dictionary<string, GeoInfo>();
        if (_host.Geo.Enabled)
        {
            StateText.Text = L.Get("Trace.LookingUp");
            try
            {
                geo = await _host.Geo.LookupManyAsync(hops.Select(h => h.Address).OfType<string>(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StateText.Text = L.F("Trace.LookupFailed", ex.Message);
            }
        }
        var report = TraceAnalysis.Analyze(hops, geo);
        foreach (var row in _rows)
        {
            if (row.Hop.Address is not { } a) continue;
            if (IPAddress.TryParse(a, out var ip) && !GeoIpService.IsPublic(ip)) row.Network = L.Get("Trace.PrivateAddress");
            if (geo.TryGetValue(a, out var g))
            {
                row.Network = g.Asn is { } asn ? $"AS{asn} · {g.Org ?? g.Isp}" : g.Org ?? g.Isp ?? "";
                row.Location = TraceAnalysis.Place(g);
            }
            if (report.HopNotes.TryGetValue(row.Number, out var note))
            {
                row.Note = note;
                if (row.Location.Length > 0) row.Location += " ?";
            }
        }
        NotesList.ItemsSource = report.Notes;
        if (StateText.Text == L.Get("Trace.LookingUp")) StateText.Text = "";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var lines = new List<string> { Header.Text, "" };
        lines.AddRange(_rows.Select(r => string.Join("  ", new[] { r.Number.ToString().PadLeft(2), r.Address, r.HostName, r.Times, r.Network, r.Location, r.Note }
            .Where(x => !string.IsNullOrEmpty(x)))));
        if (NotesList.ItemsSource is IEnumerable<string> notes)
        {
            lines.Add("");
            lines.AddRange(notes);
        }
        try
        {
            Clipboard.SetText(string.Join("\n", lines));
        }
        catch (Exception)
        {
            // the clipboard is held by another program
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
