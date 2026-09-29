using System.Windows;
using System.Windows.Controls;
using Quaykeep.Core.Geo;
using Quaykeep.Core.Models;
using Quaykeep.Services;
using Quaykeep.ViewModels;

namespace Quaykeep.Views;

/// <summary>
/// Everything known about a server's address: the network and hoster (GeoIP), the registry's record (RDAP), and the
/// full text whois from the registry's server, read when the window opens.
/// </summary>
public partial class WhoisWindow : Window
{
    private readonly AppHost _host;
    private CancellationTokenSource? _cts;
    private string? _rawFor;

    public WhoisWindow(AppHost host, Guid serverId)
    {
        InitializeComponent();
        _host = host;
        ServerId = serverId;
        _host.Vault.FactsChanged += OnFactsChanged;
        Closed += (_, _) =>
        {
            _host.Vault.FactsChanged -= OnFactsChanged;
            _cts?.Cancel();
        };
        Loaded += (_, _) =>
        {
            Show(Server());
            _ = LoadRawAsync(force: false);
        };
    }

    public Guid ServerId { get; }

    private ServerEntry? Server() => _host.Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == ServerId)?.Clone(), out var s) ? s : null;

    private void OnFactsChanged(object? sender, Guid id)
    {
        if (id != ServerId) return;
        Dispatcher.BeginInvoke(() =>
        {
            Show(Server());
            _ = LoadRawAsync(force: false); // a new address (or the first record) brings its own text
        });
    }

    private void Show(ServerEntry? s)
    {
        if (s == null)
        {
            Close();
            return;
        }
        Title = L.F("Whois.Title", s.Name);
        var f = s.Facts;
        var g = f?.Geo;
        var w = f?.Whois;
        var ip = w?.Ip ?? g?.Ip;
        Header.Text = ip != null && ip != s.Host ? $"{s.Name}  ({s.Host} → {ip})" : $"{s.Name}  ({s.Host})";
        Note.Visibility = Visibility.Collapsed;
        if (!_host.Geo.Enabled) ShowNote(L.Get("Whois.Disabled"));
        else if (w == null && g == null) ShowNote(L.Get("Whois.NotYet"));

        Fields.Children.Clear();
        Fields.RowDefinitions.Clear();
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = Fields.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 2, 16, 2) };
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = null, Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap, MinHeight = 0,
            };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            Fields.Children.Add(l);
            Fields.Children.Add(v);
        }
        Row(L.Get("Whois.Hoster"), f?.Hoster);
        if (g != null)
        {
            Row(L.Get("Whois.As"), g.Asn is { } asn ? $"AS{asn}" + (g.Org != null ? " · " + g.Org : "") : g.Org);
            if (g.Isp != null && g.Isp != g.Org) Row(L.Get("Whois.Isp"), g.Isp);
            Row(L.Get("Whois.Domain"), g.Domain);
            Row(L.Get("Whois.Location"), g.Label + (g.Latitude is { } lat && g.Longitude is { } lon ? $"  ({lat:0.###}, {lon:0.###})" : ""));
        }
        if (w != null)
        {
            Row(L.Get("Whois.Registry"), w.Registry);
            Row(L.Get("Whois.Network"), w.Network != w.Range && w.Range != null ? $"{w.Network}  ({w.Range})" : w.Network);
            Row(L.Get("Whois.NetName"), string.Join("  ", new[] { w.NetName, w.Handle != w.NetName ? w.Handle : null }.Where(x => x != null)));
            Row(L.Get("Whois.Owner"), w.OwnerHandle != null ? $"{w.Owner}  ({w.OwnerHandle})" : w.Owner);
            Row(L.Get("Whois.Address"), w.Address);
            Row(L.Get("Whois.Country"), w.Country);
            Row(L.Get("Whois.Abuse"), w.AbuseEmail);
            Row(L.Get("Whois.Description"), w.Description);
            Row(L.Get("Whois.Registered"), w.Registered?.ToLocalTime().ToString("d", L.Culture));
            Row(L.Get("Whois.Changed"), w.Changed?.ToLocalTime().ToString("d", L.Culture));
            Row(L.Get("Whois.Read"), w.Updated.ToString("g", L.Culture));
        }
        var query = HosterSearch.Query(f);
        PanelButton.Content = f?.Hoster is { } h ? L.F("Ctx.FindPanelOf", h) : L.Get("Ctx.FindPanel");
        PanelButton.IsEnabled = query != null;
    }

    private void ShowNote(string text)
    {
        Note.Text = text;
        Note.Visibility = Visibility.Visible;
    }

    /// <summary>The registry's full text record (not stored: it is long and is asked for rarely).</summary>
    private async Task LoadRawAsync(bool force)
    {
        if (!_host.Geo.Enabled) return;
        var s = Server();
        var ip = s?.Facts?.Whois?.Ip ?? s?.Facts?.Geo?.Ip;
        if (s == null) return;
        if (ip == null)
        {
            try
            {
                ip = await GeoIpService.ResolveAsync(s.Host);
            }
            catch (Exception)
            {
                ip = null;
            }
        }
        if (ip == null)
        {
            RawTitle.Text = L.Get("Whois.NoPublicIp");
            return;
        }
        if (!force && _rawFor == ip) return;
        _rawFor = ip;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var server = s.Facts?.Whois?.Port43;
        RawTitle.Text = L.F("Whois.RawLoading", server ?? "whois.iana.org");
        Busy.Visibility = Visibility.Visible;
        try
        {
            var text = await Whois.RawAsync(ip, server, cts.Token);
            if (cts.IsCancellationRequested) return;
            Raw.Text = text;
            RawTitle.Text = L.F("Whois.RawFrom", server ?? "whois.iana.org");
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            RawTitle.Text = L.F("Whois.RawFailed", ex.Message);
            _rawFor = null;
        }
        catch (Exception)
        {
        }
        finally
        {
            if (_cts == cts) Busy.Visibility = Visibility.Hidden;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _host.Geo.RefreshAsync(ServerId, force: true);
            Show(Server());
            await LoadRawAsync(force: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
        }
    }

    private void OnFindPanel(object sender, RoutedEventArgs e)
    {
        if (HosterSearch.Query(Server()?.Facts) is { } q) MainViewModel.OpenUrl(HosterSearch.Url(q));
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var lines = new List<string> { Header.Text };
        for (var i = 0; i + 1 < Fields.Children.Count; i += 2)
            if (Fields.Children[i] is TextBlock l && Fields.Children[i + 1] is TextBox v) lines.Add($"{l.Text}: {v.Text}");
        if (Raw.Text.Length > 0) lines.Add("\n" + Raw.Text);
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
