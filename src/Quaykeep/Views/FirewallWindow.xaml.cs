using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// Firewall rules of one server, of a whole group (the rules and presets then replace every server's own), or of a
/// preset (used by several servers). Edits of a server or a preset are kept in the vault at once and reach the servers
/// on Apply; a group's only on Apply.
/// </summary>
public partial class FirewallWindow : Window
{
    private enum Mode { Server, Group, Preset }

    private sealed record Row(FirewallRule Rule, bool Own, bool Enabled, string Action, string Target, string Sources, string SourcesTip,
        string Comment, string From, string Status);

    private sealed record PresetChoice(FirewallPreset Preset, bool Checked, string Label, string Tip);

    private readonly AppHost _host;
    private readonly Mode _mode;
    private readonly List<ServerEntry> _servers;
    private readonly FirewallPreset? _preset;
    /// <summary>The rules being edited: the server's (with its presets), the group's, or the preset's.</summary>
    private FirewallConfig _config;
    /// <summary>This PC as each server sees it, for the lockout warning and "+ this PC".</summary>
    private readonly Dictionary<Guid, IPAddress> _clients = [];
    /// <summary>Addresses put in the form from a server ("+ server"), by address: they become the rule's links.</summary>
    private readonly Dictionary<string, Guid> _pendingLinks = [];
    private FirewallRule? _editing;
    private bool _running;

    public FirewallWindow(AppHost host, IReadOnlyList<ServerEntry> servers, string? groupName = null)
        : this(host, groupName != null ? Mode.Group : Mode.Server, servers, null)
    {
        if (_mode == Mode.Group)
        {
            Title = L.F("Fw.GroupTitle", groupName, _servers.Count);
            HintText.Text = L.Get("Fw.GroupHint") + "\n" + L.Get("Fw.Hint");
            ApplyButton.Content = L.F("Fw.ApplyGroup", _servers.Count);
            CopyButton.Visibility = Visibility.Collapsed;
        }
        else if (_servers.FirstOrDefault() is { } s) Title = L.F("Fw.Title", s.Name);
        Header.Text = _mode == Mode.Group ? Title : _servers.FirstOrDefault() is { } first ? $"{first.Name}  ({first.Display})" : "";
    }

    /// <summary>Edits a preset; Apply goes to every server that uses it.</summary>
    public FirewallWindow(AppHost host, FirewallPreset preset) : this(host, Mode.Preset, [], preset)
    {
        Title = L.F("Fw.PresetTitle", preset.Name);
        Header.Text = Title;
        HintText.Text = L.Get("Fw.PresetHint") + "\n" + L.Get("Fw.Hint");
        PresetsBar.Visibility = CopyButton.Visibility = TurnOffButton.Visibility = Visibility.Collapsed;
        UpdatePresetApply();
    }

    private FirewallWindow(AppHost host, Mode mode, IReadOnlyList<ServerEntry> servers, FirewallPreset? preset)
    {
        InitializeComponent();
        _host = host;
        _mode = mode;
        _preset = preset;
        _servers = servers.Where(s => string.IsNullOrWhiteSpace(s.JumpHost)).Select(s => s.Clone()).ToList();
        if (preset != null) _config = new FirewallConfig { Rules = preset.Rules.Select(r => r.Clone()).ToList() };
        else
        {
            var first = _servers.FirstOrDefault(s => s.Firewall?.Rules.Count > 0 || s.Firewall?.Presets.Count > 0) ?? _servers.FirstOrDefault();
            _config = first?.Firewall?.Clone() ?? new FirewallConfig();
        }
        ShowRules();
        Closing += (_, e) => e.Cancel = _running;
        Loaded += (_, _) => { if (_mode != Mode.Preset && _servers.Count > 0) OnRefresh(this, new RoutedEventArgs()); };
    }

    // ---------- showing ----------

    private List<FirewallPreset> Presets => _host.Vault.Read(d => d.FirewallPresets.Select(p => p.Clone()).ToList());

    private ServerEntry? Current(Guid id) => _host.Vault.Read(d => d.Servers.FirstOrDefault(x => x.Id == id)?.Clone());

    /// <summary>The servers this window applies to (a preset: the ones that use it now).</summary>
    private List<ServerEntry> Targets => _mode == Mode.Preset
        ? _host.Vault.Read(d => FirewallSets.UsingPreset(d, _preset!.Id).Where(s => string.IsNullOrWhiteSpace(s.JumpHost)).Select(s => s.Clone()).ToList())
        : _servers;

    private void UpdatePresetApply()
    {
        if (_mode != Mode.Preset) return;
        var n = Targets.Count;
        ApplyButton.Content = L.F("Fw.ApplyPreset", n);
        ApplyButton.IsEnabled = !_running && n > 0;
    }

    /// <summary>"name (1.2.3.4)" for a source that is a server's address.</summary>
    private string SourceLabel(FirewallRule r, string source)
    {
        var link = r.Links.FirstOrDefault(l => l.Address == source);
        var name = link == null ? null : _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == link.ServerId)?.Name);
        return name == null ? source : $"{name} ({source})";
    }

    private void ShowRules()
    {
        var presets = Presets;
        var states = Targets.Select(s =>
        {
            var cur = Current(s.Id) ?? s;
            return FirewallRules.Status(FirewallSets.Effective(cur.Firewall, presets), cur.Facts?.Firewall);
        }).ToList();
        var applied = _mode == Mode.Server && states.FirstOrDefault() == FirewallStatus.Applied;
        string Status(FirewallRule r) => !r.Enabled ? L.Get("Fw.RuleOff") : applied ? L.Get("Fw.RuleActive") : L.Get("Fw.RulePending");
        Row MakeRow(FirewallRule r, bool own, string from) => new(r, own, r.Enabled,
            L.Get(r.Action == FirewallAction.Block ? "Fw.Block" : "Fw.AllowOnly"), FirewallRules.TargetText(r),
            string.Join(", ", r.Sources.Take(6).Select(s => SourceLabel(r, s))) + (r.Sources.Count > 6 ? $" +{r.Sources.Count - 6}" : ""),
            string.Join("\n", r.Sources.Select(s => SourceLabel(r, s))), r.Comment ?? "", from, Status(r));

        var ownFrom = _mode == Mode.Preset ? _preset!.Name : L.Get("Fw.FromOwn");
        var rows = _config.Rules.Select(r => MakeRow(r, true, ownFrom)).ToList();
        if (_mode != Mode.Preset)
            foreach (var id in _config.Presets)
                if (presets.FirstOrDefault(p => p.Id == id) is { } p)
                    rows.AddRange(p.Rules.Select(r => MakeRow(r, false, p.Name)));
        var selected = (RulesGrid.SelectedItem as Row)?.Rule.Id;
        RulesGrid.ItemsSource = rows;
        if (selected != null) RulesGrid.SelectedItem = rows.FirstOrDefault(r => r.Rule.Id == selected);

        PresetsList.ItemsSource = presets.Select(p => new PresetChoice(p, _config.Presets.Contains(p.Id),
            L.F("Fw.PresetCount", p.Name, p.Rules.Count), string.Join("\n", p.Rules.Select(FirewallRules.Describe)))).ToList();

        var targets = Targets;
        StateText.Text = _mode == Mode.Server
            ? FirewallRules.StatusText(states.FirstOrDefault(FirewallStatus.None))
            : string.Join("\n", targets.Zip(states, (s, st) => $"{s.Name}: {FirewallRules.StatusText(st)}"));
        var hostFirewalls = targets.SelectMany(s => Current(s.Id)?.Facts?.Services ?? [])
            .Where(x => x.Unit is "ufw" or "firewalld" && x.IsRunning).Select(x => x.Unit).Distinct().ToList();
        HostFirewallText.Text = hostFirewalls.Count > 0 ? L.F("Fw.HostFirewall", string.Join(", ", hostFirewalls)) : "";
        HostFirewallText.Visibility = hostFirewalls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePresetApply();
        OnSelectionChanged(this, null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        var own = (RulesGrid.SelectedItem as Row)?.Own ?? true;
        DeleteButton.IsEnabled = !_running && own;
        EditButton.Content = L.Get(own ? "Fw.Edit" : "Fw.EditPresetRule");
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
            CopyButton.IsEnabled = TurnOffButton.IsEnabled = ThisPcButton.IsEnabled = ServerButton.IsEnabled = EveryoneButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && Targets.Count > 0;
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

    /// <summary>A server's or a preset's rules are kept at once (they reach the servers on Apply); a group's on Apply.</summary>
    private void Save()
    {
        var config = _config.Clone();
        if (_mode == Mode.Preset)
        {
            var id = _preset!.Id;
            _host.Vault.Update(d =>
            {
                if (d.FirewallPresets.FirstOrDefault(p => p.Id == id) is { } p) p.Rules = config.Rules;
            });
        }
        else if (_mode == Mode.Server && _servers.Count > 0)
        {
            var id = _servers[0].Id;
            _host.Vault.Update(d =>
            {
                if (d.Servers.FirstOrDefault(s => s.Id == id) is { } s) s.Firewall = config;
            });
            _servers[0].Firewall = config;
        }
    }

    // ---------- presets ----------

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresetChoice c) return;
        if (!_config.Presets.Remove(c.Preset.Id)) _config.Presets.Add(c.Preset.Id);
        Save();
        ShowRules();
    }

    private void OnManagePresets(object sender, RoutedEventArgs e)
    {
        new FirewallPresetsWindow(_host) { Owner = this }.ShowDialog();
        _config.Presets.RemoveAll(id => Presets.All(p => p.Id != id)); // deleted meanwhile
        ShowRules();
    }

    // ---------- rules ----------

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortsBox == null || ProtoBox == null) return;
        PortsBox.IsEnabled = ProtoBox.IsEnabled = TargetBox.SelectedIndex == 0;
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
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        rule.Sources = rule.Sources.Select(FirewallRules.Canonical).Distinct().ToList();
        rule.Links = (_editing?.Links.Select(l => l.Clone()) ?? []).ToList();
        foreach (var (address, server) in _pendingLinks)
            rule.Links.Add(new FirewallLink { ServerId = server, Address = FirewallRules.Canonical(address) });
        FirewallSets.PruneLinks(rule);
        return rule;
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
        _pendingLinks.Clear();
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
        _pendingLinks.Clear();
        PortsBox.Text = SourcesBox.Text = CommentBox.Text = "";
        AddButton.Content = L.Get("Fw.Add");
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e) => StopEditing();

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not Row row) return;
        if (row.Own)
        {
            StartEditing(row.Rule);
            return;
        }
        // a preset's rule: edited in the preset, for every server that uses it
        var preset = Presets.FirstOrDefault(p => p.Rules.Any(r => r.Id == row.Rule.Id));
        if (preset == null) return;
        new FirewallWindow(_host, preset) { Owner = this }.ShowDialog();
        ShowRules();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => OnEdit(sender, e);

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row { Own: true } row) return;
        row.Rule.Enabled = !row.Rule.Enabled;
        Save();
        ShowRules();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not Row { Own: true } row) return;
        if (MessageBox.Show(this, L.Get("Fw.ConfirmDelete"), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _config.Rules.RemoveAll(r => r.Id == row.Rule.Id);
        if (_editing?.Id == row.Rule.Id) StopEditing();
        Save();
        ShowRules();
    }

    // ---------- sources ----------

    private void AddSources(IEnumerable<string> addresses)
    {
        var list = FirewallRules.ParseSources(SourcesBox.Text);
        foreach (var a in addresses.Where(a => !list.Contains(a))) list.Add(a);
        SourcesBox.Text = string.Join("\n", list);
    }

    private void OnAddThisPc(object sender, RoutedEventArgs e)
    {
        List<IPAddress> ips;
        lock (_clients) ips = _clients.Values.Distinct().ToList();
        if (ips.Count == 0)
        {
            MessageBox.Show(this, L.Get("Fw.ThisPcUnknown"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddSources(ips.Select(i => i.ToString()));
    }

    private void OnAddEveryone(object sender, RoutedEventArgs e) => AddSources(["0.0.0.0/0", "::/0"]);

    /// <summary>A menu of the servers in Quaykeep; the chosen one's IP goes in, linked to the server.</summary>
    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var own = _servers.Select(s => s.Id).ToHashSet();
        var servers = _host.Vault.Read(d => d.Servers.Where(s => _mode == Mode.Preset || !own.Contains(s.Id))
            .OrderBy(s => s.Group).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList());
        var menu = new ContextMenu { PlacementTarget = ServerButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var s in servers)
        {
            var item = new MenuItem { Header = (string.IsNullOrWhiteSpace(s.Group) ? "" : s.Group + " / ") + $"{s.Name}  —  {s.Host}" };
            item.Click += async (_, _) =>
            {
                var ip = await FirewallApplier.AddressOf(s);
                if (ip == null)
                {
                    MessageBox.Show(this, L.F("Fw.NoServerIp", s.Name), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _pendingLinks[ip] = s.Id;
                AddSources([ip]);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.Get("Fw.NoServers"), IsEnabled = false });
        menu.IsOpen = true;
    }

    // ---------- the servers ----------

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        var servers = Targets;
        await Run(log =>
        {
            foreach (var s in servers)
                try
                {
                    log(L.F("Fw.Connecting", s.Display));
                    _host.Firewall.Read(s);
                    if (_host.Firewall.ClientAddress(s) is { } ip) lock (_clients) _clients[s.Id] = ip;
                    var cur = Current(s.Id) ?? s;
                    log($"{s.Name}: {FirewallRules.StatusText(FirewallRules.Status(FirewallSets.Effective(cur.Firewall, Presets), cur.Facts?.Firewall))}");
                }
                catch (Exception ex)
                {
                    log(L.F("Fw.ResultError", s.Name, ex.Message));
                }
        });
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var targets = Targets;
        if (targets.Count == 0) return;
        if (_mode == Mode.Group)
        {
            if (MessageBox.Show(this, L.F("Fw.ConfirmGroup", targets.Count), Title, MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var ids = targets.Select(s => s.Id).ToHashSet();
            var config = _config.Clone();
            _host.Vault.Update(d =>
            {
                foreach (var s in d.Servers.Where(s => ids.Contains(s.Id))) s.Firewall = config.Clone();
            });
        }
        else if (_mode == Mode.Preset && MessageBox.Show(this, L.F("Fw.ConfirmPreset", targets.Count), Title, MessageBoxButton.OKCancel,
                     MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            FirewallRules.Validate(_config);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Run(log => FirewallApplier.Apply(_host, this, targets, _clients, log));
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
        if (_mode != Mode.Server || _servers.Count == 0) return;
        var source = _servers[0];
        var others = _host.Vault.Read(d => d.Servers.Where(s => s.Id != source.Id && string.IsNullOrWhiteSpace(s.JumpHost))
            .OrderBy(s => s.Group).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList());
        var dialog = new FirewallCopyWindow(source.Name, others) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Chosen.Count == 0) return;
        var chosen = dialog.Chosen.Select(s => s.Id).ToHashSet();
        var replace = dialog.Replace;
        var copy = _config.Clone();
        _host.Vault.Update(d =>
        {
            foreach (var s in d.Servers.Where(s => chosen.Contains(s.Id)))
            {
                var config = replace ? new FirewallConfig() : s.Firewall?.Clone() ?? new FirewallConfig();
                config.Rules.AddRange(copy.Rules.Select(r =>
                {
                    var c = r.Clone();
                    c.Id = new FirewallRule().Id;
                    return c;
                }));
                foreach (var p in copy.Presets.Where(p => !config.Presets.Contains(p))) config.Presets.Add(p);
                s.Firewall = config;
            }
        });
        await Run(log => FirewallApplier.Apply(_host, this, dialog.Chosen, _clients, log));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
