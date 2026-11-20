using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quaykeep.Core;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Core.Monitoring;
using Quaykeep.Services;

namespace Quaykeep.Views;

public partial class ServerEditorWindow : DialogWindow
{
    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private string? _manualIpv4;
    private string? _manualIpv6;
    private bool _showingDetectedIps;
    private bool _hasManualDraft;
    private bool _refreshingPublicIps;

    /// <param name="server">A copy to edit; on success it holds the new values.</param>
    public ServerEditorWindow(AppHost host, ServerEntry server, bool isNew)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Title = isNew ? L.Get("Editor.NewTitle") : L.F("Editor.EditTitle", server.Name);

        // every group path and its parents ("VPN", "VPN/Europe")
        GroupBox.ItemsSource = host.Vault.Data.Servers
            .SelectMany(s =>
            {
                var parts = ViewModels.MainViewModel.SplitGroup(s.Group);
                return Enumerable.Range(1, parts.Length).Select(n => string.Join("/", parts.Take(n)));
            })
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
        McpBox.ItemsSource = Enum.GetValues<McpAccess>().Select(a => new McpChoice(a, McpLevelText(a))).ToList();
        McpBox.SelectedIndex = (int)server.McpAccess;
        foreach (var folder in server.McpFolders) McpFolderList.Items.Add(folder);
        McpGlobalOff.IsVisible = !host.SettingsStore.Settings.Mcp.Enabled;
        McpExpander.IsExpanded = server.McpAccess != McpAccess.Off;
        OnMcpChanged(this, null);
        MonitorBox.Text = server.MonitorIntervalMinutes?.ToString() ?? "";
        MonitorHint.Text = L.F("Editor.MonitorHint", host.SettingsStore.Settings.MonitorIntervalMinutes);
        NameBox.Text = server.Name;
        GroupBox.Text = server.Group;
        HostBox.Text = server.Host;
        PortBox.Text = server.Port.ToString();
        UserBox.Text = server.Username;
        PasswordBox.Text = server.Password ?? "";
        JumpBox.Text = server.JumpHost ?? "";
        SsmProfileBox.Text = server.SsmProfile ?? "";
        SsmRegionBox.Text = server.SsmRegion ?? "";
        SsmHint.Text = Core.Ssh.AwsSsm.FindCli() == null ? L.Get("Ssm.NoCli") : L.Get("Editor.SsmHint");
        if (server.Ssm) RouteSsm.IsChecked = true;
        else if (!string.IsNullOrWhiteSpace(server.JumpHost)) RouteJump.IsChecked = true;
        else RouteDirect.IsChecked = true;
        AdvancedExpander.IsExpanded = RouteDirect.IsChecked != true;
        ExtraBox.Text = server.ExtraArgs ?? "";
        NotesBox.Text = server.Notes ?? "";
        ReloadKeys(server.KeyId);
        if (server.Auth == AuthMode.Key) AuthKey.IsChecked = true;
        else if (server.Auth == AuthMode.None && server.Ssm) AuthSsmShell.IsChecked = true;
        else AuthPassword.IsChecked = true;

        _manualIpv4 = server.ManualPublicIpv4;
        _manualIpv6 = server.ManualPublicIpv6;
        _hasManualDraft = !server.AutoDetectPublicIps;
        PublicIpv4Box.Text = _manualIpv4;
        PublicIpv6Box.Text = _manualIpv6;
        PublicIpIntervalBox.Text = server.PublicIpIntervalMinutes.ToString();
        AutoPublicIpsBox.IsChecked = server.AutoDetectPublicIps;
        AutoPublicIpsBox.IsCheckedChanged += (_, _) => RefreshPublicIps(editModeChanged: true);
        ShowPublicIps(isNew ? null : server.Facts?.PublicIps);
        RefreshPublicIpsButton.IsEnabled = !isNew && server.AutoDetectPublicIps && PublicIpMonitor.Supported(server);
        if (!RefreshPublicIpsButton.IsEnabled)
            ToolTip.SetTip(RefreshPublicIpsButton,
                L.Get(isNew || !server.AutoDetectPublicIps ? "PublicIp.SaveFirst" : "PublicIp.DetectionHint"));
        if (!isNew)
        {
            host.Vault.FactsChanged += OnPublicIpFactsChanged;
            Closed += (_, _) => host.Vault.FactsChanged -= OnPublicIpFactsChanged;
        }

        Opened += (_, _) =>
        {
            RefreshPublicIps();
            (isNew ? HostBox : NameBox).Focus();
        };
    }

    private void OnPublicIpFactsChanged(object? sender, Guid id)
    {
        if (id != _server.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) RefreshPublicIps();
        });
    }

    private void RefreshPublicIps(bool editModeChanged = false)
    {
        _host.Vault.TryRead(d =>
        {
            var saved = d.Servers.FirstOrDefault(s => s.Id == _server.Id);
            var bind = saved == null ? [] : ForwardAddresses.InterfaceAddresses(saved).Distinct().ToArray();
            return (Display: saved?.Display, Bind: bind, PublicIps: saved?.Facts?.PublicIps);
        }, out var info);
        PublicIpEndpoints.IsVisible = info.Display != null;
        if (info.Display != null)
            PublicIpEndpoints.Text = L.F("PublicIp.Endpoints", info.Display,
                info.Bind.Length == 0 ? L.Get("PublicIp.Unavailable") : string.Join(", ", info.Bind));
        if (editModeChanged || AutoPublicIpsBox.IsChecked == true) ShowPublicIps(info.PublicIps);
    }

    private async void OnRefreshPublicIps(object? sender, RoutedEventArgs e)
    {
        _refreshingPublicIps = true;
        RefreshPublicIpsButton.IsEnabled = false;
        PublicIpStatus.Text = L.Get("PublicIp.Checking");
        try
        {
            await _host.RefreshPublicIpsAsync(_server.Id);
        }
        finally
        {
            _refreshingPublicIps = false;
            RefreshPublicIpsButton.IsEnabled = true;
            RefreshPublicIps();
        }
    }

    private void ShowPublicIps(PublicIpObservation? observation)
    {
        var auto = AutoPublicIpsBox.IsChecked == true;
        if (auto && !_showingDetectedIps)
        {
            _manualIpv4 = PublicIpv4Box.Text;
            _manualIpv6 = PublicIpv6Box.Text;
        }
        else if (!auto && !_hasManualDraft)
        {
            _manualIpv4 ??= observation?.Ipv4;
            _manualIpv6 ??= observation?.Ipv6;
            _hasManualDraft = true;
        }
        _showingDetectedIps = auto;
        PublicIpv4Box.IsReadOnly = PublicIpv6Box.IsReadOnly = auto;
        PublicIpv4Box.Text = auto ? observation?.Ipv4 : _manualIpv4;
        PublicIpv6Box.Text = auto ? observation?.Ipv6 : _manualIpv6;
        if (_refreshingPublicIps) return;
        PublicIpStatus.Text = observation == null ? L.Get("PublicIp.Pending") :
            L.F("PublicIp.LastChecked", observation.CheckedUtc.ToLocalTime().ToString("g"));
        if (observation == null) return;
        if (observation.Issue != PublicIpCheckIssue.None)
            PublicIpStatus.Text += "\n" + L.Get(observation.Issue switch
            {
                PublicIpCheckIssue.HostKey => "PublicIp.HostKey",
                PublicIpCheckIssue.Ssh => "PublicIp.SshFailed",
                PublicIpCheckIssue.CurlMissing => "PublicIp.CurlMissing",
                _ => "PublicIp.Incomplete",
            });
        else
        {
            if (!observation.Ipv4Current)
                PublicIpStatus.Text += "\n" + PublicLookupError("IPv4", observation.Ipv4CurlExitCode);
            if (!observation.Ipv6Current)
                PublicIpStatus.Text += "\n" + PublicLookupError("IPv6", observation.Ipv6CurlExitCode);
        }
        if (observation is { Ipv4Current: false, Ipv4: not null })
            PublicIpStatus.Text += "\n" + L.Get("PublicIp.Ipv4LastKnown");
        if (observation is { Ipv6Current: false, Ipv6: not null })
            PublicIpStatus.Text += " " + L.Get("PublicIp.Ipv6LastKnown");
    }

    private static string PublicLookupError(string family, int? exitCode) => exitCode switch
    {
        6 => L.F("PublicIp.DnsFailed", family),
        7 => L.F("PublicIp.ConnectFailed", family),
        28 => L.F("PublicIp.TimedOut", family),
        null => L.F("PublicIp.NoReply", family),
        _ => L.F("PublicIp.CurlFailed", family, exitCode.Value),
    };

    private void ReloadKeys(Guid? select)
    {
        var keys = _host.Vault.Data.Keys.OrderBy(k => k.Name).ToList();
        KeyBox.ItemsSource = keys;
        KeyBox.SelectedItem = keys.FirstOrDefault(k => k.Id == select) ?? (keys.Count == 1 ? keys[0] : null);
    }

    private void OnAuthChanged(object? sender, RoutedEventArgs e)
    {
        KeyPanel.IsVisible = AuthKey.IsChecked == true;
        PasswordPanel.IsVisible = AuthSsmShell.IsChecked != true;
    }

    private void OnRouteChanged(object? sender, RoutedEventArgs e)
    {
        JumpPanel.IsVisible = RouteJump.IsChecked == true;
        SsmPanel.IsVisible = RouteSsm.IsChecked == true;
        // "SSM shell only" exists only with SSM
        AuthSsmShell.IsVisible = RouteSsm.IsChecked == true;
        if (RouteSsm.IsChecked != true && AuthSsmShell.IsChecked == true) AuthPassword.IsChecked = true;
    }

    // ---------- AI agent access (MCP) ----------

    private sealed record McpChoice(McpAccess Access, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Menu / list text of an access level.</summary>
    public static string McpLevelText(McpAccess a) => a switch
    {
        McpAccess.ReadOnly => L.Get("Mcp.LevelReadOnly"),
        McpAccess.Reboot => L.Get("Mcp.LevelReboot"),
        McpAccess.LimitedWrite => L.Get("Mcp.LevelLimited"),
        McpAccess.Full => L.Get("Mcp.LevelFull"),
        _ => L.Get("Mcp.LevelOff"),
    };

    public static string McpLevelHint(McpAccess a) => a switch
    {
        McpAccess.ReadOnly => L.Get("Mcp.HintReadOnly"),
        McpAccess.Reboot => L.Get("Mcp.HintReboot"),
        McpAccess.LimitedWrite => L.Get("Mcp.HintLimited"),
        McpAccess.Full => L.Get("Mcp.HintFull"),
        _ => L.Get("Mcp.HintOff"),
    };

    private void OnMcpChanged(object? sender, SelectionChangedEventArgs? e)
    {
        var access = (McpBox.SelectedItem as McpChoice)?.Access ?? McpAccess.Off;
        McpHint.Text = McpLevelHint(access);
        McpFoldersPanel.IsVisible = access == McpAccess.Full;
    }

    private void OnAddMcpFolder(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Get("Editor.McpAddFolderTitle") };
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;
        if (!McpFolderList.Items.OfType<string>().Contains(folder, StringComparer.FromComparison(AppPaths.PathComparison))) McpFolderList.Items.Add(folder);
    }

    private void OnRemoveMcpFolder(object? sender, RoutedEventArgs e)
    {
        if (McpFolderList.SelectedItem is { } item) McpFolderList.Items.Remove(item);
    }

    private void OnNewKey(object? sender, RoutedEventArgs e)
    {
        var defaultName = string.IsNullOrWhiteSpace(UserBox.Text) ? "" : $"{(UserBox.Text ?? "").Trim()}@{((NameBox.Text ?? "").Trim() is { Length: > 0 } n ? n : (HostBox.Text ?? "").Trim())}";
        var dlg = new KeyGenerateWindow(defaultName) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _host.Vault.Update(d => d.Keys.Add(dlg.Result));
        ReloadKeys(dlg.Result.Id);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var host = (HostBox.Text ?? "").Trim();
        if (host.Length == 0)
        {
            ShowError(L.Get("Editor.NeedHost"));
            return;
        }
        if (!int.TryParse((PortBox.Text ?? "").Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError(L.Get("Editor.BadPort"));
            return;
        }
        var user = (UserBox.Text ?? "").Trim();
        if (user.Length == 0)
        {
            ShowError(L.Get("Editor.NeedUser"));
            return;
        }
        var auth = AuthKey.IsChecked == true ? AuthMode.Key : AuthSsmShell.IsChecked == true ? AuthMode.None : AuthMode.Password;
        var key = KeyBox.SelectedItem as KeyEntry;
        if (auth == AuthMode.Key && key == null)
        {
            ShowError(L.Get("Editor.NeedKey"));
            return;
        }
        int? monitor = null;
        if ((MonitorBox.Text ?? "").Trim() is { Length: > 0 } m)
        {
            if (!int.TryParse(m, out var minutes) || minutes < 0)
            {
                ShowError(L.Get("Editor.BadMonitor"));
                return;
            }
            monitor = minutes;
        }
        var password = PasswordBox.Text ?? "";
        var autoPublicIps = AutoPublicIpsBox.IsChecked == true;
        var interval = _server.PublicIpIntervalMinutes;
        var ipv4 = _server.ManualPublicIpv4;
        var ipv6 = _server.ManualPublicIpv6;
        if (autoPublicIps)
        {
            if (!int.TryParse(PublicIpIntervalBox.Text, out interval) || interval < 1)
            {
                ShowError(L.Get("PublicIp.BadInterval"));
                return;
            }
        }
        else
        {
            ipv4 = PublicIpObservation.Normalize(PublicIpv4Box.Text, AddressFamily.InterNetwork);
            ipv6 = PublicIpObservation.Normalize(PublicIpv6Box.Text, AddressFamily.InterNetworkV6);
            if (ipv4 == null && !string.IsNullOrWhiteSpace(PublicIpv4Box.Text) ||
                ipv6 == null && !string.IsNullOrWhiteSpace(PublicIpv6Box.Text))
            {
                ShowError(L.Get("PublicIp.BadAddress"));
                return;
            }
        }

        _server.Name = (NameBox.Text ?? "").Trim() is { Length: > 0 } n ? n : host;
        _server.Group = string.Join("/", ViewModels.MainViewModel.SplitGroup(GroupBox.Text ?? ""));
        _server.MonitorIntervalMinutes = monitor;
        _server.AutoDetectPublicIps = autoPublicIps;
        _server.PublicIpIntervalMinutes = interval;
        _server.ManualPublicIpv4 = ipv4;
        _server.ManualPublicIpv6 = ipv6;
        _server.McpAccess = (McpBox.SelectedItem as McpChoice)?.Access ?? McpAccess.Off;
        _server.McpFolders = McpFolderList.Items.OfType<string>().ToList();
        _server.Host = host;
        _server.Port = port;
        _server.Username = user;
        _server.Auth = auth;
        _server.Password = string.IsNullOrEmpty(password) ? null : password;
        _server.KeyId = key?.Id ?? (auth == AuthMode.Password ? _server.KeyId : null);
        _server.JumpHost = RouteJump.IsChecked == true ? NullIfEmpty(JumpBox.Text) : null;
        _server.Ssm = RouteSsm.IsChecked == true;
        _server.SsmProfile = NullIfEmpty(SsmProfileBox.Text);
        _server.SsmRegion = NullIfEmpty(SsmRegionBox.Text);
        _server.ExtraArgs = NullIfEmpty(ExtraBox.Text);
        _server.Notes = NullIfEmpty(NotesBox.Text);
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }
}
