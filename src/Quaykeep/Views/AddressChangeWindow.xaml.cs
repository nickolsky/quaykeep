using System.Net;
using System.Windows;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// A server got a new address: firewall rules (of servers and presets) and port forwards on other servers that name
/// the old one are updated and applied, each server keeping its new rules only when a login to it still works.
/// </summary>
public partial class AddressChangeWindow : Window
{
    private sealed class Item(string label, FirewallUse? use, ServerEntry? forwardServer, PortForward? forward)
    {
        public string Label { get; } = label;
        public bool Checked { get; set; } = true;
        public FirewallUse? Use { get; } = use;
        public ServerEntry? ForwardServer { get; } = forwardServer;
        public PortForward? Forward { get; } = forward;
    }

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly string _old;
    private readonly string _new;
    private readonly List<Item> _items;
    private readonly Dictionary<Guid, IPAddress> _clients = [];
    private bool _running;

    private AddressChangeWindow(AppHost host, ServerEntry server, string oldAddress, string newAddress, List<Item> items)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        _old = oldAddress;
        _new = newAddress;
        _items = items;
        Header.Text = L.F("Addr.Header", server.Name, oldAddress, newAddress);
        ItemsList.ItemsSource = items;
        Closing += (_, e) => e.Cancel = _running;
    }

    /// <summary>Opens the window when anything names the old address; otherwise does nothing.</summary>
    public static void OfferIfUsed(AppHost host, Window? owner, ServerEntry server, string oldAddress, string newAddress)
    {
        if (oldAddress == newAddress) return;
        var items = host.Vault.Read(d =>
        {
            var list = new List<Item>();
            foreach (var u in FirewallSets.UsesOf(d, server.Id, oldAddress))
            {
                var where = u.ServerId is { } sid
                    ? L.F("Addr.FirewallOwn", d.Servers.FirstOrDefault(s => s.Id == sid)?.Name ?? "?", FirewallRules.Describe(u.Rule))
                    : L.F("Addr.FirewallPreset", d.FirewallPresets.FirstOrDefault(p => p.Id == u.PresetId)?.Name ?? "?", FirewallRules.Describe(u.Rule));
                list.Add(new Item(where + L.Get(u.Linked ? "Addr.Linked" : "Addr.Typed"), u, null, null));
            }
            foreach (var (s, f) in FirewallSets.ForwardsTo(d, oldAddress))
                list.Add(new Item(L.F("Addr.Forward", s.Name, f.Protocol, f.ListenPort, oldAddress, f.EffectiveTargetPort), null, s.Clone(), f));
            return list;
        });
        if (items.Count == 0) return;
        new AddressChangeWindow(host, server, oldAddress, newAddress, items) { Owner = owner }.Show();
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.Checked).ToList();
        if (chosen.Count == 0) return;
        _running = true;
        RunButton.IsEnabled = CloseButton.IsEnabled = ItemsList.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            // 1. the rules in the vault: the new address in place of the old one, linked to the server
            var rules = chosen.Where(i => i.Use != null).Select(i => i.Use!.Rule.Id).ToHashSet();
            var affected = _host.Vault.Read(d =>
            {
                var servers = new HashSet<Guid>();
                foreach (var i in chosen.Where(i => i.Use != null))
                    if (i.Use!.ServerId is { } sid) servers.Add(sid);
                    else foreach (var s in FirewallSets.UsingPreset(d, i.Use.PresetId!.Value)) servers.Add(s.Id);
                return d.Servers.Where(s => servers.Contains(s.Id) && string.IsNullOrWhiteSpace(s.JumpHost)).Select(s => s.Clone()).ToList();
            });
            _host.Vault.Update(d =>
            {
                var all = d.Servers.SelectMany(s => s.Firewall?.Rules ?? []).Concat(d.FirewallPresets.SelectMany(p => p.Rules));
                foreach (var r in all.Where(r => rules.Contains(r.Id))) FirewallSets.Replace(r, _server.Id, _old, _new);
            });
            if (rules.Count > 0) Append(L.F("Addr.RulesUpdated", rules.Count));

            var forwards = chosen.Where(i => i.Forward != null).ToList();
            await Task.Run(() =>
            {
                // 2. the firewalls of the servers that use those rules
                FirewallApplier.Apply(_host, this, affected, _clients, Append);
                // 3. the forwards: removed and added again in one script, the old one comes back when adding fails
                foreach (var i in forwards)
                {
                    var f = i.Forward!;
                    try
                    {
                        Append(L.F("Addr.UpdatingForward", i.ForwardServer!.Name, f.ListenPort, _new));
                        if (f.Protocol is not ("tcp" or "udp")) throw new InvalidOperationException(L.Get("Fwd.BadProtocol"));
                        _host.Forwards.Replace(i.ForwardServer, f, [f.Protocol], f.ListenPort, _new, f.TargetPort, Append);
                    }
                    catch (Exception ex)
                    {
                        Append(L.F("Fw.ResultError", i.ForwardServer!.Name, ex.Message));
                    }
                }
            });
            Append(L.Get("Addr.Done"));
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
        }
        finally
        {
            _running = false;
            CloseButton.IsEnabled = true;
            CloseButton.Content = L.Get("Common.Close");
            Busy.Visibility = Visibility.Hidden;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
