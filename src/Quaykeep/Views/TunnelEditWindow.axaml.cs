using Avalonia.Interactivity;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>Adds or changes one tunnel; shows the equivalent ssh / aws command as it is filled in.</summary>
public partial class TunnelEditWindow : DialogWindow
{
    private sealed record ServerChoice(ServerEntry Server)
    {
        public override string ToString() => $"{Server.Name}  ({Server.Display})";
    }

    private readonly AppHost _host;
    private readonly Tunnel _tunnel;
    private ServerEntry _server;

    /// <summary>The saved tunnel when the dialog returns true.</summary>
    public Tunnel? Result { get; private set; }

    /// <summary>The server the tunnel belongs to (the chosen one when <c>servers</c> were offered).</summary>
    public Guid ServerId => _server.Id;

    /// <param name="tunnel">A copy to change, or null for a new one.</param>
    /// <param name="servers">Servers to choose from (adding from the list of all tunnels); null = only <paramref name="server"/>.</param>
    public TunnelEditWindow(AppHost host, ServerEntry server, Tunnel? tunnel, IReadOnlyList<ServerEntry>? servers = null)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        _tunnel = tunnel ?? new Tunnel
        {
            // an SSM server without SSH login has only SSM port forwarding
            Via = server.Ssm && !server.SshLogin ? TunnelVia.Ssm : TunnelVia.Ssh,
        };
        if (servers is { Count: > 1 })
        {
            var choices = servers.Select(s => new ServerChoice(s)).ToList();
            ServerBox.ItemsSource = choices;
            ServerBox.SelectedItem = choices.FirstOrDefault(c => c.Server.Id == server.Id) ?? choices[0];
            ServerPanel.IsVisible = true;
            Header.IsVisible = false;
        }
        ShowServer();
        (_tunnel.Kind == TunnelKind.Dynamic ? KindDynamic : KindLocal).IsChecked = true;
        (_tunnel.Via == TunnelVia.Ssm ? ViaSsm : ViaSsh).IsChecked = true;
        LocalPortBox.Text = _tunnel.LocalPort > 0 ? _tunnel.LocalPort.ToString() : "";
        RemoteHostBox.Text = _tunnel.RemoteHost ?? "";
        RemotePortBox.Text = _tunnel.RemotePort > 0 ? _tunnel.RemotePort.ToString() : "";
        NameBox.Text = _tunnel.Name;
        AutoStartBox.IsChecked = _tunnel.AutoStart;
        foreach (var box in new[] { LocalPortBox, RemoteHostBox, RemotePortBox })
            box.TextChanged += (_, _) => ShowCommand();
        OnChanged(this, new RoutedEventArgs());
        Opened += (_, _) => LocalPortBox.Focus();
    }

    private void ShowServer()
    {
        Header.Text = $"{_server.Name}  ({_server.Display})";
        ViaPanel.IsVisible = _server.Ssm;
    }

    private void OnServerChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (ServerBox.SelectedItem is not ServerChoice c || ViaSsm is null) return;
        _server = c.Server;
        ShowServer();
        // what fits the newly chosen server: SSM forwarding is all a shell-only one has, plain SSH servers have no SSM
        if (!_server.Ssm) ViaSsh.IsChecked = true;
        else if (!_server.SshLogin) ViaSsm.IsChecked = true;
        OnChanged(this, new RoutedEventArgs());
    }

    private void OnChanged(object? sender, RoutedEventArgs e)
    {
        if (KindDynamic is null || ViaSsm is null) return; // during InitializeComponent
        // SOCKS needs SSH
        if (KindDynamic.IsChecked == true && ViaSsm.IsChecked == true) ViaSsh.IsChecked = true;
        ViaSsm.IsEnabled = KindDynamic.IsChecked != true;
        RemotePanel.IsVisible = KindLocal.IsChecked == true;
        ShowCommand();
    }

    /// <summary>The tunnel as the fields describe it (ports 0 when not numbers).</summary>
    private Tunnel Current()
    {
        var t = _tunnel.Clone();
        t.Kind = KindDynamic.IsChecked == true ? TunnelKind.Dynamic : TunnelKind.Local;
        t.Via = _server.Ssm && ViaSsm.IsChecked == true ? TunnelVia.Ssm : TunnelVia.Ssh;
        t.LocalPort = int.TryParse((LocalPortBox.Text ?? "").Trim(), out var lp) ? lp : 0;
        t.RemoteHost = t.Kind == TunnelKind.Local && (RemoteHostBox.Text ?? "").Trim() is { Length: > 0 } h ? h : null;
        t.RemotePort = t.Kind == TunnelKind.Local && int.TryParse((RemotePortBox.Text ?? "").Trim(), out var rp) ? rp : 0;
        t.Name = (NameBox.Text ?? "").Trim();
        t.AutoStart = AutoStartBox.IsChecked == true;
        return t;
    }

    private void ShowCommand()
    {
        var t = Current();
        string P(int port) => port > 0 ? port.ToString() : "?";
        var host = t.RemoteHost ?? "localhost";
        CommandHint.Text = t.Via == TunnelVia.Ssm
            ? $"aws ssm start-session --target {_server.Host} --document-name " +
              (t.RemoteHost == null
                  ? $"AWS-StartPortForwardingSession --parameters portNumber={P(t.RemotePort)},localPortNumber={P(t.LocalPort)}"
                  : $"AWS-StartPortForwardingSessionToRemoteHost --parameters host={host},portNumber={P(t.RemotePort)},localPortNumber={P(t.LocalPort)}") +
              (string.IsNullOrWhiteSpace(_server.SsmProfile) ? "" : " --profile " + _server.SsmProfile.Trim())
            : "ssh -N " + (t.Kind == TunnelKind.Dynamic ? $"-D {P(t.LocalPort)}" : $"-L {P(t.LocalPort)}:{host}:{P(t.RemotePort)}") +
              $" {_server.Username}@{_server.Host}" + (_server.Ssm ? "  (" + L.Get("TunnelEdit.ThroughSsm") + ")" : "");
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var t = Current();
        if (TunnelService.Problem(_server, t) is { } problem)
        {
            ShowError(problem);
            return;
        }
        if (_host.Vault.Read(d => TunnelService.SamePort(d, t)) is { } other)
        {
            ShowError(L.F("TunnelEdit.SamePort", t.LocalPort, string.IsNullOrWhiteSpace(other.Name) ? other.Route : other.Name));
            return;
        }
        Result = t;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }
}
