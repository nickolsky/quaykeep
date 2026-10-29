using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Mvvm;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>A server's tunnels (ssh -L / -D, SSM port forwarding): the list with live states, start / stop and editing.</summary>
public partial class TunnelsWindow : ToolWindow
{
    public sealed class TunnelRow(Tunnel tunnel) : ObservableObject
    {
        private TunnelStatus _status = new(TunnelState.Stopped);

        public Tunnel Tunnel { get; set; } = tunnel;
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

    private readonly AppHost _host;
    private readonly Guid _serverId;
    private readonly ObservableCollection<TunnelRow> _rows = [];

    public Guid ServerId => _serverId;

    public TunnelsWindow(AppHost host, Guid serverId)
    {
        InitializeComponent();
        _host = host;
        _serverId = serverId;
        TunnelsGrid.ItemsSource = _rows;
        var server = Server();
        Title = L.F("Tunnels.Title", server?.Name ?? "");
        Header.Text = server == null ? "" : $"{server.Name}  ({server.Display})";
        Load();
        _host.Tunnels.StateChanged += OnStateChanged;
        Closed += (_, _) => _host.Tunnels.StateChanged -= OnStateChanged;
        UpdateButtons();
    }

    private ServerEntry? Server() => _host.Vault.Data.Servers.FirstOrDefault(s => s.Id == _serverId);

    private void Load(Guid? select = null)
    {
        var keep = select ?? (TunnelsGrid.SelectedItem as TunnelRow)?.Tunnel.Id;
        _rows.Clear();
        foreach (var t in Server()?.Tunnels ?? [])
            _rows.Add(new TunnelRow(t.Clone()) { Status = _host.Tunnels.Get(t.Id) });
        TunnelsGrid.SelectedItem = _rows.FirstOrDefault(r => r.Tunnel.Id == keep) ?? _rows.FirstOrDefault();
    }

    private void OnStateChanged(object? sender, Guid id) => Dispatcher.UIThread.Post(() =>
    {
        if (_rows.FirstOrDefault(r => r.Tunnel.Id == id) is not { } row) return;
        row.Status = _host.Tunnels.Get(id);
        UpdateButtons();
    });

    private TunnelRow? Selected => TunnelsGrid.SelectedItem as TunnelRow;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var row = Selected;
        EditButton.IsEnabled = RemoveButton.IsEnabled = CopyButton.IsEnabled = ToggleButton.IsEnabled = row != null;
        ToggleButton.Content = L.Get(row != null && _host.Tunnels.IsOn(row.Tunnel.Id) ? "Tunnels.Stop" : "Tunnels.Start");
    }

    private void ShowError(string? text)
    {
        Error.Text = text ?? "";
        Error.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Server() is not { } server) return;
        var dlg = new TunnelEditWindow(_host, server.Clone(), null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } tunnel) return;
        _host.Vault.Update(d => d.Servers.First(s => s.Id == _serverId).Tunnels.Add(tunnel));
        Load(tunnel.Id);
        _host.Tunnels.Start(_serverId, tunnel.Id);
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Server() is not { } server || Selected is not { } row) return;
        var dlg = new TunnelEditWindow(_host, server.Clone(), row.Tunnel.Clone()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } tunnel) return;
        _host.Vault.Update(d =>
        {
            var list = d.Servers.First(s => s.Id == _serverId).Tunnels;
            var i = list.FindIndex(t => t.Id == tunnel.Id);
            if (i >= 0) list[i] = tunnel;
        });
        Load(tunnel.Id);
        // a running tunnel picks up the change: closed and opened again
        if (_host.Tunnels.IsOn(tunnel.Id))
        {
            _host.Tunnels.Stop(tunnel.Id);
            _host.Tunnels.Start(_serverId, tunnel.Id);
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
        _host.Vault.Update(d => d.Servers.First(s => s.Id == _serverId).Tunnels.RemoveAll(t => t.Id == row.Tunnel.Id));
        Load();
        UpdateButtons();
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        ShowError(null);
        if (_host.Tunnels.IsOn(row.Tunnel.Id)) _host.Tunnels.Stop(row.Tunnel.Id);
        else _host.Tunnels.Start(_serverId, row.Tunnel.Id);
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
