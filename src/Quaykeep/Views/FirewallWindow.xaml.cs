using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// Firewall rules of one server, or of a whole group (then the rules replace every server's own). Edits of a single
/// server are saved in the vault at once and reach the server on Apply; a group's only on Apply.
/// </summary>
public partial class FirewallWindow : Window
{
    private sealed record Row(FirewallRule Rule, bool Enabled, string Action, string Target, string Sources, string SourcesTip,
        string Comment, string Status);

    private readonly AppHost _host;
    private readonly List<ServerEntry> _servers;
    private readonly bool _group;
    private FirewallConfig _config;
    /// <summary>This PC as each server sees it (read on Refresh), for the lockout warning.</summary>
    private readonly Dictionary<Guid, IPAddress> _clients = [];
    private FirewallRule? _editing;
    private bool _running;

    public FirewallWindow(AppHost host, IReadOnlyList<ServerEntry> servers, string? groupName = null)
    {
        InitializeComponent();
        _host = host;
        _servers = servers.Where(s => string.IsNullOrWhiteSpace(s.JumpHost)).Select(s => s.Clone()).ToList();
        _group = groupName != null;
        var first = _servers.FirstOrDefault(s => s.Firewall?.Rules.Count > 0) ?? _servers.FirstOrDefault();
        _config = first?.Firewall?.Clone() ?? new FirewallConfig();
        if (_group)
        {
            Title = L.F("Fw.GroupTitle", groupName, _servers.Count);
            Header.Text = Title;
            HintText.Text = L.Get("Fw.GroupHint") + "\n" + L.Get("Fw.Hint");
            ApplyButton.Content = L.F("Fw.ApplyGroup", _servers.Count);
            CopyButton.Visibility = Visibility.Collapsed;
        }
        else if (first != null)
        {
            Title = L.F("Fw.Title", first.Name);
            Header.Text = $"{first.Name}  ({first.Display})";
        }
        ApplyButton.IsEnabled = _servers.Count > 0;
        ShowRules();
        Closing += (_, e) => e.Cancel = _running;
        Loaded += (_, _) => { if (_servers.Count > 0) OnRefresh(this, new RoutedEventArgs()); };
    }

    // ---------- showing ----------

    private ServerEntry? Current(ServerEntry s) => _host.Vault.Read(d => d.Servers.FirstOrDefault(x => x.Id == s.Id)?.Clone());

    private void ShowRules()
    {
        var states = _servers.Select(s => FirewallRules.Status(_group ? s.Firewall : _config, Current(s)?.Facts?.Firewall)).ToList();
        var applied = !_group && states.FirstOrDefault() == FirewallStatus.Applied;
        var selected = (RulesGrid.SelectedItem as Row)?.Rule.Id;
        RulesGrid.ItemsSource = _config.Rules.Select(r => new Row(r, r.Enabled,
            L.Get(r.Action == FirewallAction.Block ? "Fw.Block" : "Fw.AllowOnly"), FirewallRules.TargetText(r),
            FirewallRules.SourcesText(r, 6), string.Join("\n", r.Sources), r.Comment ?? "",
            !r.Enabled ? L.Get("Fw.RuleOff") : applied ? L.Get("Fw.RuleActive") : L.Get("Fw.RulePending"))).ToList();
        if (selected != null) RulesGrid.SelectedItem = ((List<Row>)RulesGrid.ItemsSource).FirstOrDefault(r => r.Rule.Id == selected);

        StateText.Text = _group
            ? string.Join("\n", _servers.Zip(states, (s, st) => $"{s.Name}: {FirewallRules.StatusText(st)}"))
            : FirewallRules.StatusText(states.FirstOrDefault(FirewallStatus.None));
        var hostFirewalls = _servers.SelectMany(s => Current(s)?.Facts?.Services ?? [])
            .Where(x => x.Unit is "ufw" or "firewalld" && x.IsRunning).Select(x => x.Unit).Distinct().ToList();
        HostFirewallText.Text = hostFirewalls.Count > 0 ? L.F("Fw.HostFirewall", string.Join(", ", hostFirewalls)) : "";
        HostFirewallText.Visibility = hostFirewalls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private void SetBusy(bool busy)
    {
        _running = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        AddButton.IsEnabled = CancelEditButton.IsEnabled = RefreshButton.IsEnabled = EditButton.IsEnabled = DeleteButton.IsEnabled =
            CopyButton.IsEnabled = TurnOffButton.IsEnabled = ThisPcButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _servers.Count > 0;
    }

    private async Task Run(Action<Action<string>> work)
    {
        SetBusy(true);
        try
        {
            await Task.Run(() => work(Append));
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            ShowRules();
        }
    }

    /// <summary>Single server: the rules are kept at once (they reach the server on Apply).</summary>
    private void Save()
    {
        if (_group || _servers.Count == 0) return;
        var id = _servers[0].Id;
        var config = _config.Clone();
        _host.Vault.Update(d =>
        {
            if (d.Servers.FirstOrDefault(s => s.Id == id) is { } s) s.Firewall = config;
        });
        _servers[0].Firewall = config;
    }

    // ---------- rules ----------

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortsBox == null) return;
        var ports = TargetBox.SelectedIndex == 0;
        PortsBox.IsEnabled = ProtoBox.IsEnabled = ports;
    }

    private FirewallRule? RuleFromForm()
    {
        var rule = new FirewallRule
        {
            Action = ActionBox.SelectedIndex == 0 ? FirewallAction.Block : FirewallAction.AllowOnly,
            Ports = TargetBox.SelectedIndex == 0 ? FirewallRules.NormalizePorts(PortsBox.Text) : "",
            Protocol = ProtoBox.SelectedIndex switch { 1 => "udp", 2 => "tcp,udp", _ => "tcp" },
            Sources = FirewallRules.ParseSources(SourcesBox.Text),
            Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim(),
        };
        try
        {
            FirewallRules.Validate(rule);
            rule.Sources = rule.Sources.Select(FirewallRules.Canonical).Distinct().ToList();
            return rule;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (RuleFromForm() is not { } rule) return;
        if (_editing != null)
        {
            rule.Id = _editing.Id;
            rule.Enabled = _editing.Enabled;
            var i = _config.Rules.FindIndex(r => r.Id == _editing.Id);
            if (i >= 0) _config.Rules[i] = rule;
            else _config.Rules.Add(rule);
        }
        else _config.Rules.Add(rule);
        Save();
        StopEditing();
        ShowRules();
        RulesGrid.SelectedItem = ((List<Row>)RulesGrid.ItemsSource).FirstOrDefault(r => r.Rule.Id == rule.Id);
    }

    private void StartEditing(FirewallRule r)
    {
        _editing = r;
        ActionBox.SelectedIndex = r.Action == FirewallAction.Block ? 0 : 1;
        TargetBox.SelectedIndex = r.WholeServer ? 1 : 0;
        ProtoBox.SelectedIndex = r.Protocol switch { "udp" => 1, "tcp,udp" => 2, _ => 0 };
        PortsBox.Text = r.WholeServer ? "" : FirewallRules.NormalizePorts(r.Ports).Replace(':', '-');
        SourcesBox.Text = string.Join("\n", r.Sources);
        CommentBox.Text = r.Comment ?? "";
        AddButton.Content = L.Get("Fw.SaveRule");
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void StopEditing()
    {
        _editing = null;
        PortsBox.Text = SourcesBox.Text = CommentBox.Text = "";
        AddButton.Content = L.Get("Fw.Add");
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e) => StopEditing();

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is Row row) StartEditing(row.Rule);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => OnEdit(sender, e);

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row) return;
        row.Rule.Enabled = !row.Rule.Enabled;
        Save();
        ShowRules();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not Row row) return;
        if (MessageBox.Show(this, L.Get("Fw.ConfirmDelete"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _config.Rules.RemoveAll(r => r.Id == row.Rule.Id);
        if (_editing?.Id == row.Rule.Id) StopEditing();
        Save();
        ShowRules();
    }

    private void OnAddThisPc(object sender, RoutedEventArgs e)
    {
        if (_clients.Values.Distinct().ToList() is not { Count: > 0 } ips)
        {
            MessageBox.Show(this, L.Get("Fw.ThisPcUnknown"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var list = FirewallRules.ParseSources(SourcesBox.Text);
        foreach (var ip in ips.Select(i => i.ToString()).Where(i => !list.Contains(i))) list.Add(ip);
        SourcesBox.Text = string.Join("\n", list);
    }

    // ---------- the server ----------

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        var servers = _servers.ToList();
        await Run(log =>
        {
            foreach (var s in servers)
                try
                {
                    log(L.F("Fw.Connecting", s.Display));
                    _host.Firewall.Read(s);
                    if (_host.Firewall.ClientAddress(s) is { } ip) lock (_clients) _clients[s.Id] = ip;
                    log($"{s.Name}: {FirewallRules.StatusText(FirewallRules.Status(_group ? s.Firewall : _config, Current(s)?.Facts?.Firewall))}");
                }
                catch (Exception ex)
                {
                    log(L.F("Fw.ResultError", s.Name, ex.Message));
                }
        });
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_servers.Count == 0) return;
        if (_group && MessageBox.Show(this, L.F("Fw.ConfirmGroup", _servers.Count), Title, MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await ApplyTo(_servers.Select(s => (s, _config.Clone())).ToList());
    }

    /// <summary>One server after another; each keeps its rules only when a new login to it works.</summary>
    private async Task ApplyTo(List<(ServerEntry Server, FirewallConfig Config)> targets)
    {
        try
        {
            foreach (var (_, config) in targets) FirewallRules.Validate(config);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Run(log =>
        {
            foreach (var (server, config) in targets)
            {
                try
                {
                    if (!_clients.ContainsKey(server.Id) && _host.Firewall.ClientAddress(server) is { } found)
                        lock (_clients) _clients[server.Id] = found;
                    if (_clients.TryGetValue(server.Id, out var me) && FirewallRules.CutsSsh(config, server.Port, me) &&
                        !Dispatcher.Invoke(() => MessageBox.Show(this, L.F("Fw.ConfirmCutsSsh", server.Name, me), Title,
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK))
                        continue;
                    var id = server.Id;
                    _host.Vault.Update(d =>
                    {
                        if (d.Servers.FirstOrDefault(s => s.Id == id) is { } s) s.Firewall = config.Clone();
                    });
                    server.Firewall = config.Clone();
                    log(L.F("Fw.Applying", server.Name));
                    var r = _host.Firewall.Apply(server, config, log);
                    log(L.F(r.Applied ? "Fw.ResultApplied" : "Fw.ResultReverted", server.Name));
                }
                catch (Exception ex)
                {
                    log(L.F("Fw.ResultError", server.Name, ex.Message));
                }
            }
        });
    }

    private async void OnTurnOff(object sender, RoutedEventArgs e)
    {
        if (_servers.Count == 0) return;
        var names = string.Join(", ", _servers.Select(s => s.Name));
        if (MessageBox.Show(this, L.F("Fw.ConfirmTurnOff", names), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var servers = _servers.ToList();
        await Run(log =>
        {
            foreach (var s in servers)
                try
                {
                    _host.Firewall.TurnOff(s, log);
                }
                catch (Exception ex)
                {
                    log(L.F("Fw.ResultError", s.Name, ex.Message));
                }
        });
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_group || _servers.Count == 0) return;
        var source = _servers[0];
        var others = _host.Vault.Read(d => d.Servers.Where(s => s.Id != source.Id && string.IsNullOrWhiteSpace(s.JumpHost))
            .OrderBy(s => s.Group).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList());
        var dialog = new FirewallCopyWindow(source.Name, others) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Chosen.Count == 0) return;
        var targets = dialog.Chosen.Select(s =>
        {
            var copies = _config.Rules.Select(r =>
            {
                var c = r.Clone();
                c.Id = new FirewallRule().Id;
                return c;
            });
            var config = dialog.Replace ? new FirewallConfig() : s.Firewall?.Clone() ?? new FirewallConfig();
            config.Rules.AddRange(copies);
            return (s, config);
        }).ToList();
        await ApplyTo(targets);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
