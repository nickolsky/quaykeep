using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Mvvm;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// Tunnels (ssh -L / -D, SSM port forwarding) with live states, start / stop and editing: of one server (its menu) or of
/// every server (View → All tunnels, the tray), with Start all / Stop all and a "running only" filter.
/// </summary>
public partial class TunnelsWindow : ToolWindow
{
    public sealed class TunnelRow(Guid serverId, string serverName, Tunnel tunnel) : ObservableObject
    {
        private TunnelStatus _status = new(TunnelState.Stopped);

        public Guid ServerId { get; } = serverId;
        public string ServerName { get; } = serverName;
        public Tunnel Tunnel { get; } = tunnel;
        public string Name => Tunnel.Name;
        public string Route => Tunnel.Route;
        public string Via => Tunnel.Via == TunnelVia.Ssm ? "AWS SSM" : "SSH";
        public string Auto => Tunnel.AutoStart ? L.Get("Common.Yes") : "";

        public TunnelStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                foreach (var n in new[] { nameof(StateText), nameof(Error), nameof(IsIdle), nameof(IsRunning), nameof(IsBusy), nameof(IsFailed) })
                    OnPropertyChanged(n);
            }
        }

        public string StateText => L.Get(Status.State switch
        {
            TunnelState.Starting => "Tunnel.State.Starting",
            TunnelState.Running => "Tunnel.State.Running",
            TunnelState.Retrying => "Tunnel.State.Retrying",
            TunnelState.Failed => "Tunnel.State.Failed",
            _ => "Tunnel.State.Stopped",
        });

        public string? Error => Status.Error;
        public bool IsIdle => Status.State == TunnelState.Stopped;
        public bool IsRunning => Status.State == TunnelState.Running;
        public bool IsBusy => Status.State is TunnelState.Starting or TunnelState.Retrying;
        public bool IsFailed => Status.State == TunnelState.Failed;
    }

    /// <summary>The Server column (columns get no fields from x:Name).</summary>
    private const int ServerColumnIndex = 1;

    private readonly AppHost _host;
    private readonly Guid? _serverId;
    private readonly ObservableCollection<TunnelRow> _rows = [];

    /// <summary>The server whose tunnels are shown; null = every server.</summary>
    public Guid? ServerId => _serverId;

    /// <param name="serverId">One server's tunnels, or null for all of them.</param>
    public TunnelsWindow(AppHost host, Guid? serverId)
    {
        InitializeComponent();
        _host = host;
        _serverId = serverId;
        TunnelsGrid.ItemsSource = _rows;
        if (serverId == null)
        {
            Title = L.Get("Tunnels.AllTitle");
            Header.Text = L.Get("Tunnels.AllTitle");
            TunnelsGrid.Columns[ServerColumnIndex].IsVisible = true;
            RunningOnly.IsVisible = true;
        }
        else
        {
            var server = Servers().FirstOrDefault();
            Title = L.F("Tunnels.Title", server?.Name ?? "");
            Header.Text = server == null ? "" : $"{server.Name}  ({server.Display})";
        }
        Load();
        _host.Tunnels.StateChanged += OnStateChanged;
        _host.Vault.DataChanged += OnDataChanged;
        Closed += (_, _) =>
        {
            _host.Tunnels.StateChanged -= OnStateChanged;
            _host.Vault.DataChanged -= OnDataChanged;
        };
    }

    /// <summary>The servers this window covers, by name.</summary>
    private List<ServerEntry> Servers() =>
        _host.Vault.TryRead(d => d.Servers.Where(s => _serverId == null || s.Id == _serverId)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList(), out var list)
            ? list
            : [];

    private void Load(Guid? select = null)
    {
        var keep = select ?? (TunnelsGrid.SelectedItem as TunnelRow)?.Tunnel.Id;
        var runningOnly = RunningOnly.IsChecked == true;
        _rows.Clear();
        foreach (var s in Servers())
        foreach (var t in s.Tunnels)
            if (!runningOnly || _host.Tunnels.IsOn(t.Id))
                _rows.Add(new TunnelRow(s.Id, s.Name, t) { Status = _host.Tunnels.Get(t.Id) });
        TunnelsGrid.SelectedItem = _rows.FirstOrDefault(r => r.Tunnel.Id == keep) ?? _rows.FirstOrDefault();
        UpdateButtons();
    }

    private void OnDataChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Load());

    private void OnStateChanged(object? sender, Guid id) => Dispatcher.UIThread.Post(() =>
    {
        var row = _rows.FirstOrDefault(r => r.Tunnel.Id == id);
        // the filter shows tunnels that are on: one starting elsewhere appears, a stopped one leaves
        if (RunningOnly.IsChecked == true && (row == null) == _host.Tunnels.IsOn(id))
        {
            Load();
            return;
        }
        if (row == null) return;
        row.Status = _host.Tunnels.Get(id);
        UpdateButtons();
    });

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => Load();

    private TunnelRow? Selected => TunnelsGrid.SelectedItem as TunnelRow;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var row = Selected;
        EditButton.IsEnabled = RemoveButton.IsEnabled = CopyButton.IsEnabled = ToggleButton.IsEnabled = row != null;
        ToggleButton.Content = L.Get(row != null && _host.Tunnels.IsOn(row.Tunnel.Id) ? "Tunnels.Stop" : "Tunnels.Start");
        StartAllButton.IsEnabled = _rows.Any(r => !_host.Tunnels.IsOn(r.Tunnel.Id));
        StopAllButton.IsEnabled = _rows.Any(r => _host.Tunnels.IsOn(r.Tunnel.Id));
    }

    private void ShowError(string? text)
    {
        Error.Text = text ?? "";
        Error.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var servers = Servers();
        if (servers.Count == 0) return;
        // from the list of every server: start with the selected row's server, and let the editor offer the others
        var first = servers.FirstOrDefault(s => s.Id == Selected?.ServerId) ?? servers[0];
        var dlg = new TunnelEditWindow(_host, first, null, _serverId == null ? servers : null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } tunnel) return;
        var serverId = dlg.ServerId;
        _host.Vault.Update(d => d.Servers.First(s => s.Id == serverId).Tunnels.Add(tunnel));
        Load(tunnel.Id);
        _host.Tunnels.Start(serverId, tunnel.Id);
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || Servers().FirstOrDefault(s => s.Id == row.ServerId) is not { } server) return;
        var dlg = new TunnelEditWindow(_host, server, row.Tunnel.Clone()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } tunnel) return;
        _host.Vault.Update(d =>
        {
            var list = d.Servers.First(s => s.Id == row.ServerId).Tunnels;
            var i = list.FindIndex(t => t.Id == tunnel.Id);
            if (i >= 0) list[i] = tunnel;
        });
        Load(tunnel.Id);
        // a running tunnel picks up the change: closed and opened again
        if (_host.Tunnels.IsOn(tunnel.Id))
        {
            _host.Tunnels.Stop(tunnel.Id);
            _host.Tunnels.Start(row.ServerId, tunnel.Id);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var name = string.IsNullOrWhiteSpace(row.Name) ? row.Route : row.Name;
        if (MessageBox.Show(this, L.F("Tunnels.ConfirmRemove", name), Title ?? "", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _host.Tunnels.Stop(row.Tunnel.Id);
        _host.Vault.Update(d => d.Servers.First(s => s.Id == row.ServerId).Tunnels.RemoveAll(t => t.Id == row.Tunnel.Id));
        Load();
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        ShowError(null);
        if (_host.Tunnels.IsOn(row.Tunnel.Id)) _host.Tunnels.Stop(row.Tunnel.Id);
        else _host.Tunnels.Start(row.ServerId, row.Tunnel.Id);
        UpdateButtons();
    }

    private void OnStartAll(object? sender, RoutedEventArgs e)
    {
        ShowError(null);
        foreach (var row in _rows.ToList()) _host.Tunnels.Start(row.ServerId, row.Tunnel.Id);
        UpdateButtons();
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.ToList()) _host.Tunnels.Stop(row.Tunnel.Id);
        UpdateButtons();
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        Views.Clipboard.SetText(row.Tunnel.Kind == TunnelKind.Dynamic
            ? $"socks5://127.0.0.1:{row.Tunnel.LocalPort}"
            : $"127.0.0.1:{row.Tunnel.LocalPort}");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
