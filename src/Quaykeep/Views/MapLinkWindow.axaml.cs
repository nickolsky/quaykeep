using System.Net;
using System.Net.Sockets;
using Avalonia.Interactivity;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Views;

/// <summary>
/// A link drawn on the network map: the forward's ports and target, checked against what is already there and against
/// the target's firewall. Nothing goes to a server here; the forward joins the map's plan.
/// </summary>
public partial class MapLinkWindow : DialogWindow
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
        PublicIpsText.Text = string.Join("\n", new[] { from.Server, to?.Server }.OfType<ServerEntry>()
            .Select(server => PublicIpSummary.Format(server, includeName: true)));
        ListenLabel.Text = L.F("Map.PortOn", from.Name);
        TargetPortLabel.Text = L.F("Map.PortOn", to?.Name ?? L.Get("Map.Target"));
        ListenIpBox.ItemsSource = ForwardAddresses.Listen(from.Server!).ToList();
        if (to?.Server is { } target)
        {
            var choices = ForwardAddresses.Targets(target);
            TargetBox.ItemsSource = choices.Select(a => ForwardTargetChoice.From(target, a, includeName: false)).ToList();
            var near = AddressBook.AddressToward(from.Server!, target);
            var preferred = choices.FirstOrDefault(a => a.Address == near) ??
                choices.FirstOrDefault(a => a.Source is ForwardAddressSource.Interface or ForwardAddressSource.Ssh);
            TargetBox.Text = preferred?.Address ?? "";
        }
        else if (to != null)
        {
            TargetBox.ItemsSource = new[] { new ForwardTargetChoice(to.Address, to.Address) };
            TargetBox.Text = to.Address;
        }
        ProtoBox.SelectionChanged += OnChanged;
        ListenBox.TextChanged += OnChanged;
        ListenIpBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.ComboBox.TextProperty) Evaluate();
        };
        TargetPortBox.TextChanged += OnChanged;
        TargetBox.SelectionChanged += OnChanged;
        TargetBox.LostFocus += OnChanged;
        TargetBox.KeyUp += OnChanged;
        TargetBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.ComboBox.TextProperty) Evaluate();
        };
        AllowBox.Click += OnChanged;
        Loaded += (_, _) =>
        {
            ListenBox.Focus();
            Evaluate();
        };
    }

    private string[] Protocols => ProtoBox.SelectedIndex switch { 1 => ["udp"], 2 => ["tcp", "udp"], _ => ["tcp"] };

    private string TargetText => (TargetBox.Text ?? "").Trim();
    private string ListenIp => (ListenIpBox.Text ?? "").Trim();

    private void OnChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>Shows who may connect, whether the target's firewall lets the hop through, and what stops the link.</summary>
    private void Evaluate()
    {
        if (!IsLoaded) return;
        var from = _from.Server!;
        var listen = FirewallRules.NormalizePorts((ListenBox.Text ?? "").Trim());
        var targetPort = FirewallRules.NormalizePorts((TargetPortBox.Text ?? "").Trim());
        var arrive = targetPort.Length > 0 ? targetPort : listen;
        var ip = TargetText;
        var listenIp = ListenIp;
        var family = RelayCommands.ParseAddress(listenIp)?.AddressFamily;
        var problems = new List<string>();
        var warnings = new List<string>();
        _fix = null;
        AllowBox.IsVisible = false;
        EntryText.Text = VerdictText.Text = "";

        if (listen.Length == 0 || ip.Length == 0) problems.Add(L.Get("Fwd.FillFields"));
        else
        {
            var isIp = IPAddress.TryParse(ip.Trim('[', ']'), out var literal);
            var destination = isIp ? (ip.Contains('%') ? ip : literal!.ToString()) :
                family == AddressFamily.InterNetwork ? "2001:db8::1" : "192.0.2.1";
            foreach (var p in Protocols)
            {
                PortForward candidate;
                try
                {
                    candidate = PortForwardService.Validate(p, listen, destination, targetPort, listenIp);
                }
                catch (ArgumentException ex)
                {
                    problems.Add(ex.Message);
                    break;
                }
                foreach (var e in _map.Edges.Where(e => e.From.Id == from.Id && !e.Removing &&
                                                        PortForwardService.Conflicts(e.Forward, candidate)))
                    problems.Add(L.F("Map.PortTaken", from.Name, e.Forward.Protocol, e.Forward.ListenPort, e.To.Name, e.Forward.EffectiveTargetPort));
            }
            if (listenIp.Length == 0 && Protocols.Contains("tcp") && ForwardChains.Overlaps(listen, from.Port.ToString()))
                problems.Add(L.F("Map.PortIsSsh", from.Port, from.Name));
            else if (Protocols.Contains("tcp") && from.Facts?.ListeningPorts.FirstOrDefault(l => ForwardChains.Overlaps(listen, l.Port.ToString())) is { } used)
                warnings.Add(L.F("Map.PortUsed", from.Name, used.Port, used.Process ?? "?"));

            if (problems.Count == 0)
            {
                var protocol = Protocols[0];
                EntryText.Text = L.F("Map.Entry", from.Name, listen, FirewallCheck.AccessText(FirewallCheck.Access(_from.Firewall!, protocol, listen, family)));
                var target = isIp ? _book.Find(destination, from) : _to?.Server;
                var node = target == null ? null : _map.NodeOf(target);
                if (!isIp) VerdictText.Text = L.Get("Map.ResolvedOnAdd");
                else if (node?.Firewall == null) VerdictText.Text = L.Get("Map.VerdictNotServer");
                else Describe(node, protocol, arrive, AddressBook.SourceToward(from, destination), warnings);
            }
        }
        ProblemText.Text = string.Join("\n", problems.Distinct());
        ProblemText.IsVisible = problems.Count > 0;
        WarningText.Text = string.Join("\n", warnings);
        WarningText.IsVisible = warnings.Count > 0;
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
            AllowBox.IsVisible = true;
        }
        else warnings.Add(L.F(own ? "Map.BlockedByBlock" : "Map.BlockedByPreset", target.Name));
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Evaluate();
        if (!OkButton.IsEnabled) return;
        var listenIp = ListenIp;
        var listen = FirewallRules.NormalizePorts((ListenBox.Text ?? "").Trim());
        var targetPort = FirewallRules.NormalizePorts((TargetPortBox.Text ?? "").Trim());
        try
        {
            var ip = PortForwardService.ResolveTarget(TargetText, listenIp);
            var forwards = Protocols.Select(p => PortForwardService.Validate(p, listen, ip, targetPort, listenIp)).ToList();
            foreach (var f in forwards)
            {
                Forwards.Add(new PlannedForward { FromId = _from.Server!.Id, Protocol = f.Protocol, ListenPort = f.ListenPort,
                    ListenIp = f.ListenIp, TargetIp = f.TargetIp, TargetPort = f.TargetPort });
            }
        }
        catch (Exception ex)
        {
            ProblemText.Text = ex.Message;
            ProblemText.IsVisible = true;
            return;
        }
        if (_fix != null && AllowBox.IsChecked == true)
            Allow = new PlannedAllow
            {
                ServerId = _fix.ServerId, RuleId = _fix.RuleId, Source = _fix.Source, SourceServerId = _fix.SourceServerId, ForId = Forwards[0].Id,
            };
        DialogResult = true;
    }
}
