using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Services;
using Quaykeep.ViewModels;

namespace Quaykeep.Views;

public partial class PortForwardWindow : Window
{
    private sealed record Row(PortForward Forward, string Protocol, string ListenPort, string Target, string Chain, string ChainTip,
        string Interface, string Owner, bool Monitored, bool CanMonitor, string MonitorTip);

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly MainViewModel _vm;
    private List<PortForward> _forwards = [];
    /// <summary>The forward being changed in the form, null while adding a new one.</summary>
    private PortForward? _editing;
    private bool _running;

    public PortForwardWindow(AppHost host, ServerEntry server, MainViewModel vm)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        _vm = vm;
        Header.Text = $"{server.Name}  ({server.Display})";
        PublicIpsText.Track(this, host.Vault, new HashSet<Guid> { server.Id });
        RefreshListenChoices();
        ListenIpBox.DropDownOpened += (_, _) => RefreshListenChoices();
        RefreshTargetChoices();
        TargetBox.DropDownOpened += (_, _) => RefreshTargetChoices();
        ShowForwards(server.Facts?.Forwards ?? []);
        Closing += (_, e) => e.Cancel = _running;
        Loaded += (_, _) => OnRefresh(this, new RoutedEventArgs());
        ForwardsGrid.DoubleTapped += OnRowDoubleClick;
        // the check boxes inside the template: their clicks bubble up here
        ForwardsGrid.AddHandler(Button.ClickEvent, (_, e) => { if (e.Source is CheckBox cb) OnMonitorClick(cb, e); });
    }

    private void RefreshListenChoices()
    {
        if (!_host.Vault.TryRead(d => ForwardAddresses.Listen(d.Servers.FirstOrDefault(s => s.Id == _server.Id) ?? _server)
                .ToList(), out var choices)) return;
        if (ListenIpBox.ItemsSource is List<string> current && current.SequenceEqual(choices)) return;
        var text = ListenIpBox.Text;
        ListenIpBox.ItemsSource = choices;
        ListenIpBox.Text = text;
    }

    private void RefreshTargetChoices()
    {
        if (!_host.Vault.TryRead(d => d.Servers.Where(s => s.Id != _server.Id)
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(s => ForwardAddresses.Targets(s).Select(a => ForwardTargetChoice.From(s, a, includeName: true)))
                .ToList(), out var choices)) return;
        if (TargetBox.ItemsSource is List<ForwardTargetChoice> current && current.SequenceEqual(choices)) return;
        var text = TargetBox.Text;
        TargetBox.ItemsSource = choices;
        TargetBox.Text = text;
    }

    private void ShowForwards(List<PortForward> forwards)
    {
        _forwards = forwards;
        var monitored = _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == _server.Id)?.MonitoredPorts.Select(p => p.Port).ToHashSet()) ?? [];
        var selected = (ForwardsGrid.SelectedItem as Row)?.Forward.Key;
        ForwardsGrid.ItemsSource = forwards.Select(f =>
        {
            var name = _vm.TargetName(f.TargetIp);
            var chain = _vm.ForwardChain(_server, f);
            var single = int.TryParse(f.ListenPort, out var port);
            var canMonitor = !f.IsRelay && single && f.Protocol == "tcp";
            return new Row(f, f.Protocol, f.ListenEndpoint,
                PortForward.Endpoint(name, f.EffectiveTargetPort) + (name != f.TargetIp ? $"  ({f.TargetIp})" : ""),
                ForwardChains.Format(chain), ForwardChains.FormatLines(chain),
                f.InInterface ?? "", f.Managed ? "Quaykeep" : L.Get("Fwd.External"),
                canMonitor && monitored.Contains(port), canMonitor,
                canMonitor ? L.F("Fwd.MonitorTip", f.ListenPort) : L.Get(f.IsRelay ? "Fwd.MonitorRelay" : single ? "Fwd.MonitorUdp" : "Fwd.MonitorRange"));
        }).ToList();
        if (selected != null) ForwardsGrid.SelectedItem = ((List<Row>)ForwardsGrid.ItemsSource!).FirstOrDefault(r => r.Forward.Key == selected);
        if (_editing != null && forwards.All(f => f.Key != _editing.Key)) StopEditing(); // changed or removed meanwhile
    }

    private void Append(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            Log.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
            Log.CaretIndex = Log.Text?.Length ?? 0;
        });

    private async Task<bool> Run(Func<Action<string>, ForwardChange?> work)
    {
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => work(Append));
            if (result != null)
            {
                ShowForwards(result.Forwards);
                PersistButton.IsVisible = result.PersistedWith == null;
            }
            return true;
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _running = busy;
        Busy.Opacity = busy ? 1 : 0;
        AddButton.IsEnabled = RefreshButton.IsEnabled = DeleteButton.IsEnabled = EditButton.IsEnabled = PersistButton.IsEnabled =
            CancelEditButton.IsEnabled = !busy;
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        var server = _server;
        await Run(log =>
        {
            log(L.F("Fwd.Reading", server.Display));
            var list = _host.Forwards.List(server);
            log(L.F("Fwd.Found", list.Count));
            return new ForwardChange(list, "-");
        });
    }

    // ---------- add / change ----------

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var listen = (ListenBox.Text ?? "").Trim();
        var listenIp = (ListenIpBox.Text ?? "").Trim();
        var targetText = (TargetBox.Text ?? "").Trim();
        var targetPort = (TargetPortBox.Text ?? "").Trim();
        string[] protocols = ProtoBox.SelectedIndex switch { 1 => ["udp"], 2 => ["tcp", "udp"], _ => ["tcp"] };
        if (listen.Length == 0 || targetText.Length == 0)
        {
            Append(L.Get("Fwd.FillFields"));
            return;
        }
        var server = _server;
        var old = _editing;
        var ok = await Run(log =>
        {
            var ip = PortForwardService.ResolveTarget(targetText, listenIp);
            foreach (var p in protocols) PortForwardService.Validate(p, listen, ip, targetPort, listenIp);
            if (old != null)
            {
                log(L.F("Fwd.Changing", old.Protocol, old.ListenPort, string.Join("+", protocols), listen, ip, targetPort.Length == 0 ? listen : targetPort));
                var changed = _host.Forwards.Replace(server, old, protocols, listen, ip, targetPort, log, listenIp);
                log(L.Get("Fwd.Changed"));
                return changed;
            }
            if (listenIp.Length > 0)
            {
                var added = _host.Forwards.AddRelays(server, protocols, listenIp, listen, ip, targetPort, log);
                log(L.Get("Fwd.Added"));
                return added;
            }
            ForwardChange? last = null;
            foreach (var p in protocols)
            {
                log(L.F("Fwd.Adding", p, listen, ip, targetPort.Length == 0 ? listen : targetPort));
                last = _host.Forwards.Add(server, p, listen, ip, targetPort, log);
            }
            log(L.Get("Fwd.Added"));
            return last;
        });
        if (!ok) return;
        if (old != null)
        {
            if (!old.IsRelay) MoveMonitoring(old, listen, listenIp.Length == 0 ? protocols : []);
            StopEditing();
        }
    }

    /// <summary>A monitored listen port follows the forward when its port changes (and stops when it is no longer TCP).</summary>
    private void MoveMonitoring(PortForward old, string listen, string[] protocols)
    {
        if (!int.TryParse(old.ListenPort, out var was) || old.ListenPort == listen && protocols.Contains("tcp")) return;
        var id = _server.Id;
        var moved = false;
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == id);
            var m = s?.MonitoredPorts.FirstOrDefault(p => p.Port == was);
            if (s == null || m == null || _forwards.Any(f => !f.IsRelay && f.ListenPort == old.ListenPort && f.Protocol == "tcp")) return;
            s.MonitoredPorts.Remove(m);
            if (protocols.Contains("tcp") && int.TryParse(listen, out var now) && s.MonitoredPorts.All(p => p.Port != now))
                s.MonitoredPorts.Add(new MonitoredPort { Port = now, Name = m.Name });
            moved = true;
        });
        if (!moved) return;
        _host.Health.CheckNow(id);
        ShowForwards(_forwards);
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (ForwardsGrid.SelectedItem is Row row) StartEditing(row.Forward);
    }

    private void OnRowDoubleClick(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual).FindSelfOrAncestor<CheckBox>() != null) return;
        if (!_running && ForwardsGrid.SelectedItem is Row row) StartEditing(row.Forward);
    }

    private void StartEditing(PortForward f)
    {
        _editing = f;
        ProtoBox.SelectedIndex = f.Protocol == "udp" ? 1 : 0;
        ListenBox.Text = f.ListenPort;
        ListenIpBox.SelectedItem = null;
        ListenIpBox.Text = f.ListenIp ?? "";
        TargetBox.SelectedItem = null;
        TargetBox.Text = f.TargetIp;
        TargetPortBox.Text = f.TargetPort == f.ListenPort ? "" : f.TargetPort;
        FormTitle.Text = L.F("Fwd.EditTitle", f.Protocol, f.ListenPort, _vm.TargetName(f.TargetIp), f.EffectiveTargetPort);
        AddButton.Content = L.Get("Fwd.Save");
        CancelEditButton.IsVisible = true;
        ListenBox.Focus();
        ListenBox.SelectAll();
    }

    private void StopEditing()
    {
        _editing = null;
        FormTitle.Text = L.Get("Fwd.AddTitle");
        AddButton.Content = L.Get("Fwd.Add");
        CancelEditButton.IsVisible = false;
        ListenIpBox.SelectedItem = null;
        ListenBox.Text = TargetPortBox.Text = ListenIpBox.Text = "";
        TargetBox.SelectedItem = null;
        TargetBox.Text = "";
    }

    private void OnCancelEdit(object? sender, RoutedEventArgs e) => StopEditing();

    // ---------- monitoring ----------

    /// <summary>
    /// Monitoring of the listen port on this server: the check connects through the forward, so it covers the whole chain
    /// up to the service at its end.
    /// </summary>
    private void OnMonitorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Row { CanMonitor: true } row } || !int.TryParse(row.Forward.ListenPort, out var port)) return;
        var on = !row.Monitored;
        var id = _server.Id;
        var name = "→ " + row.Target.Split("  (")[0];
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.MonitoredPorts.RemoveAll(p => p.Port == port);
            if (on) s.MonitoredPorts.Add(new MonitoredPort { Port = port, Name = name });
        });
        _host.Health.CheckNow(id);
        Append(L.F(on ? "Port.MonitorOn" : "Port.MonitorOff", port, _server.Name));
        ShowForwards(_forwards);
    }

    // ---------- delete / persistence ----------

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (ForwardsGrid.SelectedItem is not Row row) return;
        var f = row.Forward;
        if (MessageBox.Show(this, L.F("Fwd.DeleteConfirm", f.Protocol, f.ListenEndpoint, row.Target), Title ?? "",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var server = _server;
        await Run(log =>
        {
            log(L.F("Fwd.Deleting", f.Protocol, f.ListenPort));
            var r = _host.Forwards.Remove(server, f, log);
            log(L.Get("Fwd.Deleted"));
            return r;
        });
    }

    private async void OnInstallPersistence(object? sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.Get("Fwd.InstallPersistConfirm"), Title ?? "", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var server = _server;
        await Run(log =>
        {
            log(L.Get("Fwd.InstallingPersist"));
            return _host.Forwards.InstallPersistence(server, log);
        });
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
