using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Views;

/// <summary>
/// A link drawn on the network map: the forward's ports and target, checked against what is already there and against
/// the target's firewall. Nothing goes to a server here; the forward joins the map's plan.
/// </summary>
public partial class MapLinkWindow : Window
{
    private readonly NetworkMap _map;
    private readonly MapNode _from;
    private readonly MapNode? _to;
    private readonly AddressBook _book;
    private PlannedAllow? _fix;

    public List<PlannedForward> Forwards { get; } = [];
    public PlannedAllow? Allow { get; private set; }

    /// <param name="to">The node the link was dropped on; null for an address typed in.</param>
    public MapLinkWindow(NetworkMap map, AddressBook book, MapNode from, MapNode? to)
    {
        InitializeComponent();
        _map = map;
        _book = book;
        _from = from;
        _to = to;
        Header.Text = L.F("Map.LinkHeader", from.Name, to?.Name ?? L.Get("Map.OtherAddress"));
        ListenLabel.Text = L.F("Map.PortOn", from.Name);
        TargetPortLabel.Text = L.F("Map.PortOn", to?.Name ?? L.Get("Map.Target"));
        var choices = new List<string>();
        if (to?.Server is { } target)
        {
            if (AddressBook.AddressToward(from.Server!, target) is { } near) choices.Add(near);
            if (AddressBook.PublicAddress(target) is { } pub) choices.Add(pub.ToString());
            choices.AddRange((target.Facts?.Addresses ?? []).Select(a => a.Split('/')[0]).Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork));
            if (!IPAddress.TryParse(target.Host, out _)) choices.Add(target.Host);
        }
        else if (to != null) choices.Add(to.Address);
        TargetBox.ItemsSource = choices.Distinct().ToList();
        if (choices.Count > 0) TargetBox.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            ListenBox.Focus();
            Evaluate();
        };
    }

    private string[] Protocols => ProtoBox.SelectedIndex switch { 1 => ["udp"], 2 => ["tcp", "udp"], _ => ["tcp"] };

    private string TargetText => (TargetBox.SelectedItem as string ?? TargetBox.Text).Trim();

    private void OnChanged(object sender, RoutedEventArgs e) => Evaluate();

    /// <summary>Shows who may connect, whether the target's firewall lets the hop through, and what stops the link.</summary>
    private void Evaluate()
    {
        if (!IsLoaded) return;
        var from = _from.Server!;
        var listen = FirewallRules.NormalizePorts(ListenBox.Text.Trim());
        var targetPort = FirewallRules.NormalizePorts(TargetPortBox.Text.Trim());
        var arrive = targetPort.Length > 0 ? targetPort : listen;
        var ip = TargetText;
        var problems = new List<string>();
        var warnings = new List<string>();
        _fix = null;
        AllowBox.Visibility = Visibility.Collapsed;
        EntryText.Text = VerdictText.Text = "";

        if (listen.Length == 0 || ip.Length == 0) problems.Add(L.Get("Fwd.FillFields"));
        else
        {
            var isIp = IPAddress.TryParse(ip, out _);
            foreach (var p in Protocols)
            {
                try
                {
                    IptablesCommands.Validate(p, listen, isIp ? ip : "192.0.2.1", targetPort);
                }
                catch (ArgumentException ex)
                {
                    problems.Add(ex.Message);
                    break;
                }
                foreach (var e in _map.Edges.Where(e => e.From.Id == from.Id && !e.Removing &&
                                                        (e.Forward.Protocol == p || e.Forward.Protocol == "all") &&
                                                        ForwardChains.Overlaps(e.Forward.ListenPort, listen)))
                    problems.Add(L.F("Map.PortTaken", from.Name, e.Forward.Protocol, e.Forward.ListenPort, e.To.Name, e.Forward.EffectiveTargetPort));
            }
            if (Protocols.Contains("tcp") && ForwardChains.Overlaps(listen, from.Port.ToString()))
                problems.Add(L.F("Map.PortIsSsh", from.Port, from.Name));
            else if (Protocols.Contains("tcp") && from.Facts?.ListeningPorts.FirstOrDefault(l => ForwardChains.Overlaps(listen, l.Port.ToString())) is { } used)
                warnings.Add(L.F("Map.PortUsed", from.Name, used.Port, used.Process ?? "?"));

            if (problems.Count == 0)
            {
                var protocol = Protocols[0];
                EntryText.Text = L.F("Map.Entry", from.Name, listen, FirewallCheck.AccessText(FirewallCheck.Access(_from.Firewall!, protocol, listen)));
                var target = isIp ? _book.Find(ip, from) : _to?.Server;
                var node = target == null ? null : _map.NodeOf(target);
                if (!isIp) VerdictText.Text = L.Get("Map.ResolvedOnAdd");
                else if (node?.Firewall == null) VerdictText.Text = L.Get("Map.VerdictNotServer");
                else Describe(node, protocol, arrive, AddressBook.SourceToward(from, ip), warnings);
            }
        }
        ProblemText.Text = string.Join("\n", problems.Distinct());
        ProblemText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = string.Join("\n", warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = problems.Count == 0;
    }

    private void Describe(MapNode node, string protocol, string port, IPAddress? source, List<string> warnings)
    {
        var target = node.Server!;
        var from = _from.Server!;
        var v = FirewallCheck.Check(node.Firewall!, protocol, port, source);
        VerdictText.Text = MapTexts.Verdict(target, from, v, source, port);
        if (v.Kind != HopKind.Open && node.FirewallStatus is not (FirewallStatus.Applied or FirewallStatus.None))
            VerdictText.Text += "\n" + L.Get("Map.VerdictNotApplied");
        if (v.Kind != HopKind.Blocked || source == null) return;
        var own = target.Firewall?.Rules.Any(r => r.Id == v.Rule!.Id) == true;
        if (own && v.Rule!.Action == FirewallAction.AllowOnly)
        {
            _fix = new PlannedAllow { ServerId = target.Id, RuleId = v.Rule.Id, Source = source.ToString(), SourceServerId = from.Id };
            AllowText.Text = L.F("Map.AllowFix", source, target.Name, from.Name);
            AllowBox.Visibility = Visibility.Visible;
        }
        else warnings.Add(L.F(own ? "Map.BlockedByBlock" : "Map.BlockedByPreset", target.Name));
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Evaluate();
        if (!OkButton.IsEnabled) return;
        var ip = TargetText;
        if (!IPAddress.TryParse(ip, out _))
        {
            try
            {
                ip = Dns.GetHostAddresses(ip).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                     ?? throw new InvalidOperationException();
            }
            catch (Exception)
            {
                ProblemText.Text = L.F("Fwd.CannotResolve", TargetText);
                ProblemText.Visibility = Visibility.Visible;
                return;
            }
        }
        var listen = FirewallRules.NormalizePorts(ListenBox.Text.Trim());
        var targetPort = FirewallRules.NormalizePorts(TargetPortBox.Text.Trim());
        foreach (var p in Protocols)
            Forwards.Add(new PlannedForward { FromId = _from.Server!.Id, Protocol = p, ListenPort = listen, TargetIp = ip, TargetPort = targetPort == listen ? "" : targetPort });
        if (_fix != null && AllowBox.IsChecked == true)
            Allow = new PlannedAllow
            {
                ServerId = _fix.ServerId, RuleId = _fix.RuleId, Source = _fix.Source, SourceServerId = _fix.SourceServerId, ForId = Forwards[0].Id,
            };
        DialogResult = true;
    }
}
