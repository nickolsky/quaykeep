using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Services;
using Quaykeep.ViewModels;

namespace Quaykeep.Views;

/// <summary>
/// The port forwards of all servers as a diagram: servers and addresses as nodes, forwards as arrows, and what each
/// hop's firewall does to them. Links drawn between nodes and forwards marked for removal wait in a plan until Apply.
/// </summary>
public partial class NetworkMapWindow : Window
{
    private sealed record MapTag(string Kind, string Key);

    private const double Margin0 = 40;
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly MapPlan _plan = new();
    private readonly Dictionary<Guid, IPAddress> _clients = [];
    private readonly Dictionary<string, Border> _nodeViews = [];
    private readonly List<UIElement> _edgeViews = [];
    private NetworkMap _map = new();
    private AddressBook _book = new([]);
    private Dictionary<string, MapPosition> _pos = [];
    private string? _selectedNode;
    private string? _selectedEdge;
    private bool _running;
    private bool _rebuildQueued;

    // mouse gestures
    private string? _dragNode;
    private Point _dragStart;
    private MapPosition? _dragOrigin;
    private bool _moved;
    private string? _linkFrom;
    private Line? _rubber;
    private Point? _panStart;
    private Point _panOffset;

    /// <param name="select">A node to select (a server's <see cref="MapNode.KeyOf(ServerEntry)"/>), or an edge's key.</param>
    public NetworkMapWindow(AppHost host, MainViewModel vm, string? select = null)
    {
        InitializeComponent();
        _host = host;
        _vm = vm;
        if (select?.Contains('|') == true) _selectedEdge = select;
        else _selectedNode = select;
        _host.Vault.DataChanged += OnVaultChanged;
        _host.Vault.FactsChanged += OnFactsChanged;
        _host.Vault.LockStateChanged += OnLockChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _host.Vault.DataChanged -= OnVaultChanged;
            _host.Vault.FactsChanged -= OnFactsChanged;
            _host.Vault.LockStateChanged -= OnLockChanged;
        };
        Loaded += (_, _) =>
        {
            Rebuild();
            BuildLegend();
            if (_selectedNode != null || _selectedEdge != null) ScrollToSelection();
        };
    }

    // ---------- data ----------

    private void OnVaultChanged(object? s, EventArgs e) => QueueRebuild();
    private void OnFactsChanged(object? s, Guid id) => QueueRebuild();
    private void OnLockChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() => { if (!_host.Vault.IsUnlocked) Close(); });

    /// <summary>Facts of many servers change one after another during a refresh: one rebuild for the lot.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _rebuildQueued = false;
            if (_dragNode == null && _linkFrom == null) Rebuild();
        });
    }

    private void Rebuild()
    {
        if (!_host.Vault.TryRead(d =>
            {
                // what the plan names may be gone meanwhile (a server deleted, a forward removed elsewhere)
                _plan.Adds.RemoveAll(p => d.Servers.All(s => s.Id != p.FromId));
                _plan.Removals.RemoveAll(r => d.Servers.FirstOrDefault(s => s.Id == r.ServerId)?.Facts?.Forwards.Any(f => f.Key == r.ForwardKey) != true);
                _plan.Allows.RemoveAll(a => d.Servers.FirstOrDefault(s => s.Id == a.ServerId)?.Firewall?.Rules.Any(r => r.Id == a.RuleId) != true);
                return (NetworkMap.Build(d, _plan, OnlyLinkedBox.IsChecked == true), new AddressBook(d.Servers), new Dictionary<string, MapPosition>(d.MapLayout));
            }, out var r))
            return;
        (_map, _book, var saved) = r;
        _pos = _map.Layout(saved);
        if (_selectedNode != null && _map.Node(_selectedNode) == null) _selectedNode = null;
        if (_selectedEdge != null && _map.Edges.All(e => e.Key != _selectedEdge)) _selectedEdge = null;
        Draw();
        ShowDetails();
        UpdatePlanBar();
    }

    private MapEdge? SelectedEdge => _selectedEdge == null ? null : _map.Edges.FirstOrDefault(e => e.Key == _selectedEdge);

    /// <summary>The highlighted route (the selected forward's), or a selected node's forwards; null = nothing is highlighted.</summary>
    private HashSet<MapEdge>? Highlighted()
    {
        if (SelectedEdge is { } edge) return _map.Route(edge);
        if (_selectedNode == null) return null;
        return _map.Edges.Where(e => MapNode.KeyOf(e.From) == _selectedNode || e.To.Key == _selectedNode).ToHashSet();
    }

    // ---------- drawing ----------

    private void Draw()
    {
        Board.Children.Clear();
        _nodeViews.Clear();
        _edgeViews.Clear();
        EmptyText.Visibility = _map.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var lit = Highlighted();
        DrawEdges(lit);
        foreach (var n in _map.Nodes) DrawNode(n, lit);
        SizeBoard();
    }

    private void SizeBoard()
    {
        if (_pos.Count == 0) return;
        Board.Width = _pos.Values.Max(p => p.X) + NetworkMap.NodeWidth + Margin0 * 2;
        Board.Height = _pos.Values.Max(p => p.Y) + NetworkMap.NodeHeight + Margin0 * 2 + 30;
    }

    private Rect RectOf(string key)
    {
        var p = _pos.TryGetValue(key, out var v) ? v : new MapPosition();
        var h = _nodeViews.TryGetValue(key, out var view) && view.ActualHeight > 0 ? view.ActualHeight : NetworkMap.NodeHeight;
        return new Rect(p.X + Margin0, p.Y + Margin0, NetworkMap.NodeWidth, h);
    }

    private void DrawNode(MapNode n, HashSet<MapEdge>? lit)
    {
        var server = n.Server;
        var box = new Border
        {
            Width = NetworkMap.NodeWidth, MinHeight = NetworkMap.NodeHeight, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(n.Key == _selectedNode ? 2.5 : 1.2), Padding = new Thickness(12, 8, 14, 8),
            Tag = new MapTag("node", n.Key), Cursor = Cursors.SizeAll,
        };
        box.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        box.SetResourceReference(Border.BorderBrushProperty, n.Key == _selectedNode ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush");
        if (server == null)
        {
            box.SetResourceReference(Border.BorderBrushProperty, n.Key == _selectedNode ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            box.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        }
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = n.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = server != null ? server.Host : n.Near != null ? L.F("Map.OnNetworkOf", n.Near.Name) : L.Get("Map.NotAServer");
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        if (server != null && FirewallBox.IsChecked == true && (n.HasRules || n.FirewallStatus != FirewallStatus.None))
        {
            var count = n.Firewall!.Rules.Count(r => r.Enabled);
            var badge = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) };
            badge.Inlines.Add(new System.Windows.Documents.Run(" ") { FontFamily = (FontFamily)FindResource("IconFont") });
            badge.Inlines.Add(new System.Windows.Documents.Run(L.F("Map.RulesBadge", count, L.Get(n.FirewallStatus switch
            {
                FirewallStatus.Applied => "Map.StatusApplied",
                FirewallStatus.Changed => "Map.StatusChanged",
                FirewallStatus.NotApplied => "Map.StatusNotApplied",
                FirewallStatus.Unknown => "Map.StatusUnknown",
                _ => "Map.StatusNone",
            }))));
            badge.SetResourceReference(TextBlock.ForegroundProperty, n.FirewallStatus switch
            {
                FirewallStatus.Applied => "SystemFillColorSuccessBrush",
                FirewallStatus.Changed or FirewallStatus.NotApplied => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush",
            });
            badge.ToolTip = string.Join("\n", n.Firewall.Rules.Where(r => r.Enabled).Select(FirewallRules.Describe));
            stack.Children.Add(badge);
        }
        var grid = new Grid();
        grid.Children.Add(stack);
        if (server != null && string.IsNullOrWhiteSpace(server.JumpHost))
        {
            // the handle: drag from it to another node to draw a forward
            var handle = new Ellipse
            {
                Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, -22, 0), Cursor = Cursors.Cross, Tag = new MapTag("handle", n.Key), ToolTip = L.Get("Map.HandleTip"),
                StrokeThickness = 2,
            };
            handle.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
            handle.SetResourceReference(Shape.StrokeProperty, "SolidBackgroundFillColorBaseBrush");
            grid.Children.Add(handle);
        }
        box.Child = grid;
        box.ToolTip = NodeTip(n);
        box.ContextMenu = NodeMenu(n);
        if (lit != null && n.Key != _selectedNode && !lit.Any(e => MapNode.KeyOf(e.From) == n.Key || e.To.Key == n.Key)) box.Opacity = 0.4;
        var r = RectOf(n.Key);
        Canvas.SetLeft(box, r.X);
        Canvas.SetTop(box, r.Y);
        Panel.SetZIndex(box, 10);
        Board.Children.Add(box);
        _nodeViews[n.Key] = box;
    }

    private static string NodeTip(MapNode n)
    {
        if (n.Server is not { } s) return n.Near != null ? L.F("Map.PrivateTip", n.Address, n.Near.Name) : n.Address;
        var lines = new List<string> { $"{s.Name}  ({s.Display})" };
        if (s.Facts?.Addresses is { Count: > 0 } a) lines.Add(L.F("Map.Addresses", string.Join(", ", a)));
        lines.Add(s.Facts?.InventoryUpdated is { } t ? L.F("Map.InfoFrom", t.ToString("g")) : L.Get("Map.NoInfo"));
        if (!string.IsNullOrWhiteSpace(s.JumpHost)) lines.Add(L.Get("Inventory.JumpHost"));
        return string.Join("\n", lines);
    }

    private void DrawEdges(HashSet<MapEdge>? lit)
    {
        foreach (var v in _edgeViews) Board.Children.Remove(v);
        _edgeViews.Clear();
        var firewall = FirewallBox.IsChecked == true;
        // arrows between the same two nodes bend apart
        var groups = _map.Edges.GroupBy(e =>
        {
            var a = MapNode.KeyOf(e.From);
            var b = e.To.Key;
            return string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        });
        foreach (var g in groups)
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++) DrawEdge(list[i], g.Key.Item1, g.Key.Item2, (i - (list.Count - 1) / 2.0) * 38, lit, firewall);
        }
    }

    private void DrawEdge(MapEdge e, string keyA, string keyB, double bend, HashSet<MapEdge>? lit, bool firewall)
    {
        var fromKey = MapNode.KeyOf(e.From);
        if (!_pos.ContainsKey(fromKey) || !_pos.ContainsKey(e.To.Key)) return;
        var from = RectOf(fromKey);
        var to = RectOf(e.To.Key);
        Point start, end, control;
        if (fromKey == e.To.Key)
        {
            // to itself: a loop over the node
            start = new Point(from.Right - 30, from.Top);
            end = new Point(from.Left + 30, from.Top);
            control = new Point(from.Left + from.Width / 2, from.Top - 70 - Math.Abs(bend));
        }
        else
        {
            var ca = Center(RectOf(keyA));
            var cb = Center(RectOf(keyB));
            var d = cb - ca;
            var normal = d.Length < 1 ? new Vector(0, -1) : new Vector(-d.Y, d.X) / d.Length;
            var mid = new Point((Center(from).X + Center(to).X) / 2, (Center(from).Y + Center(to).Y) / 2);
            control = mid + normal * bend * 2;
            start = Edge(from, control);
            end = Edge(to, control);
        }

        var brushKey = firewall && !e.Removing ? e.Verdict?.Kind switch
        {
            HopKind.Blocked => "SystemFillColorCriticalBrush",
            HopKind.Allowed => "SystemFillColorSuccessBrush",
            HopKind.Unknown => "SystemFillColorCautionBrush",
            _ => null,
        } : null;
        brushKey ??= e.Removing ? "SystemFillColorCriticalBrush" : e.Planned != null ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush";
        var onRoute = lit?.Contains(e) == true;
        var faded = lit != null && !onRoute;
        var tag = new MapTag("edge", e.Key);

        var geometry = new PathGeometry([new PathFigure(start, [new QuadraticBezierSegment(control, end, true)], false)]);
        var hit = new Path { Data = geometry, Stroke = Brushes.Transparent, StrokeThickness = 14, Tag = tag, Cursor = Cursors.Hand };
        var line = new Path { Data = geometry, StrokeThickness = onRoute ? 3.5 : 2, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, brushKey);
        if (e.Planned != null) line.StrokeDashArray = [5, 3];
        if (e.Removing) line.StrokeDashArray = [2, 2];
        var dir = end - control;
        if (dir.Length < 1) dir = end - start;
        dir.Normalize();
        var side = new Vector(-dir.Y, dir.X);
        var tip = end;
        var arrow = new Polygon { Points = [tip, tip - dir * 11 + side * 5.5, tip - dir * 11 - side * 5.5], IsHitTestVisible = false };
        arrow.SetResourceReference(Shape.FillProperty, brushKey);

        var mark = firewall && !e.Removing ? e.Verdict?.Kind switch
        {
            HopKind.Blocked => "✕ ",
            HopKind.Allowed => "✓ ",
            HopKind.Unknown => "? ",
            _ => "",
        } : "";
        var text = new TextBlock { Text = (e.Planned != null ? "+ " : "") + mark + e.Label, FontSize = 11.5 };
        if (e.Planned != null) text.FontStyle = FontStyles.Italic;
        if (e.Removing) text.TextDecorations = TextDecorations.Strikethrough;
        var label = new Border
        {
            Child = text, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), BorderThickness = new Thickness(onRoute ? 1.5 : 1),
            Tag = tag, Cursor = Cursors.Hand, ToolTip = EdgeTip(e),
        };
        label.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        label.SetResourceReference(Border.BorderBrushProperty, brushKey);
        hit.ToolTip = label.ToolTip;
        hit.ContextMenu = label.ContextMenu = EdgeMenu(e);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = new Point(0.25 * start.X + 0.5 * control.X + 0.25 * end.X, 0.25 * start.Y + 0.5 * control.Y + 0.25 * end.Y);
        Canvas.SetLeft(label, at.X - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, at.Y - label.DesiredSize.Height / 2);

        var parts = new List<UIElement> { hit, line, arrow, label };
        // who may connect where clients come in (the shield), when the firewall is shown
        if (firewall && e.Entry.Kind != AccessKind.Everyone && _map.IsEntry(e))
        {
            var first = control - start;
            if (first.Length < 1) first = end - start;
            first.Normalize();
            var shield = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Tag = tag, Cursor = Cursors.Hand,
                ToolTip = L.F("Map.Entry", e.From.Name, e.Forward.ListenPort, FirewallCheck.AccessText(e.Entry, 8)),
            };
            shield.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            var p = start + first * 16;
            Canvas.SetLeft(shield, p.X - 7);
            Canvas.SetTop(shield, p.Y - 20);
            parts.Add(shield);
        }
        foreach (var part in parts)
        {
            if (faded) part.Opacity = 0.18;
            Panel.SetZIndex(part, part == label ? 5 : onRoute ? 3 : 1);
            Board.Children.Add(part);
            _edgeViews.Add(part);
        }
    }

    private string EdgeTip(MapEdge e)
    {
        var lines = new List<string> { $"{e.From.Name} {e.Forward.Protocol} {e.Forward.ListenPort} → {e.To.Name}:{e.Forward.EffectiveTargetPort}" };
        if (e.Planned != null) lines.Add(L.Get("Map.StatePlanned"));
        if (e.Removing) lines.Add(L.Get("Map.StateRemoving"));
        if (VerdictLine(e) is { } v) lines.Add(v);
        return string.Join("\n", lines);
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Where the line from the rectangle's centre towards <paramref name="toward"/> leaves it.</summary>
    private static Point Edge(Rect r, Point toward)
    {
        var c = Center(r);
        var d = toward - c;
        if (Math.Abs(d.X) < 0.01 && Math.Abs(d.Y) < 0.01) return c;
        var sx = Math.Abs(d.X) < 0.01 ? double.MaxValue : r.Width / 2 / Math.Abs(d.X);
        var sy = Math.Abs(d.Y) < 0.01 ? double.MaxValue : r.Height / 2 / Math.Abs(d.Y);
        var s = Math.Min(sx, sy);
        return c + d * Math.Min(s, 1);
    }

    // ---------- texts ----------

    /// <summary>What the target's firewall does to the hop, in words (null when the target is not a server in Quaykeep).</summary>
    private static string? VerdictLine(MapEdge e)
    {
        if (e.Verdict is not { } v || e.To.Server is not { } target) return null;
        return MapTexts.Verdict(target, e.From, v, e.Source, e.Forward.EffectiveTargetPort) +
               (v.Kind != HopKind.Open && e.To.FirewallStatus is not (FirewallStatus.Applied or FirewallStatus.None) ? "\n" + L.Get("Map.VerdictNotApplied") : "");
    }

    // ---------- details panel ----------

    private void ShowDetails()
    {
        Details.Children.Clear();
        if (SelectedEdge is { } edge) ShowEdge(edge);
        else if (_selectedNode != null && _map.Node(_selectedNode) is { } node) ShowNode(node);
        else
        {
            AddTitle(L.Get("Map.Title"));
            AddLine(L.F("Map.Summary", _map.Nodes.Count(n => n.Server != null), _map.Edges.Count(e => e.Planned == null)), hint: true);
            AddLine(L.Get("Map.DetailsHint"), hint: true);
            var cut = _map.Edges.Where(e => !e.Removing && e.Verdict?.Kind == HopKind.Blocked).ToList();
            if (cut.Count > 0)
            {
                AddHeader(L.Get("Map.CutHeader"));
                foreach (var e in cut) AddLink($"{e.From.Name} → {e.To.Name}:{e.Forward.EffectiveTargetPort}", () => Select(null, e.Key));
            }
        }
    }

    private void ShowNode(MapNode n)
    {
        AddTitle(n.Name);
        if (n.Server is not { } s)
        {
            AddLine(n.Near != null ? L.F("Map.PrivateTip", n.Address, n.Near.Name) : L.F("Map.AddressTip", n.Address), hint: true);
            AddHeader(L.Get("Map.Incoming"));
            foreach (var e in _map.Edges.Where(e => e.To.Key == n.Key)) AddLink($"{e.From.Name}: {e.Label}", () => Select(null, e.Key));
            return;
        }
        AddLine(s.Display, hint: true);
        if (!string.IsNullOrWhiteSpace(s.Group)) AddLine(s.Group, hint: true);
        AddLine(s.Facts?.Addresses is { Count: > 0 } a ? L.F("Map.Addresses", string.Join(", ", a)) : L.Get("Map.NoAddresses"), hint: true);
        AddLine(s.Facts?.InventoryUpdated is { } t ? L.F("Map.InfoFrom", t.ToString("g")) : L.Get("Map.NoInfo"), hint: true);

        AddHeader(L.Get("Main.Firewall"));
        AddLine(FirewallRules.StatusText(n.FirewallStatus));
        foreach (var r in n.Firewall!.Rules.Where(r => r.Enabled)) AddLine("• " + FirewallRules.Describe(r), hint: true);

        var outgoing = _map.Edges.Where(e => e.From.Id == s.Id).ToList();
        var incoming = _map.Edges.Where(e => e.To.Key == n.Key && e.From.Id != s.Id).ToList();
        if (outgoing.Count > 0) AddHeader(L.Get("Map.Outgoing"));
        foreach (var e in outgoing) AddLink($"{e.Label} → {e.To.Name}" + PlanMark(e), () => Select(null, e.Key));
        if (incoming.Count > 0) AddHeader(L.Get("Map.Incoming"));
        foreach (var e in incoming) AddLink($"{e.From.Name}: {e.Label}" + PlanMark(e), () => Select(null, e.Key));

        AddHeader("");
        if (string.IsNullOrWhiteSpace(s.JumpHost)) AddButton(L.Get("Map.PlanFrom"), () => OpenLink(n, null));
        AddButton(L.Get("Ctx.PortForwards"), () => OpenForwards(s));
        AddButton(L.Get("Ctx.Firewall"), () => OpenFirewall(s));
        AddButton(L.Get("Map.RefreshServer"), () => RefreshServers([s]));
    }

    private static string PlanMark(MapEdge e) => e.Planned != null ? "  (+)" : e.Removing ? "  (−)" : "";

    private void ShowEdge(MapEdge edge)
    {
        AddTitle($"{edge.From.Name} → {edge.To.Name}");
        AddLine($"{edge.Forward.Protocol} {edge.Forward.ListenPort} → {edge.Forward.TargetIp}:{edge.Forward.EffectiveTargetPort}");
        AddLine(edge.Planned != null ? L.Get("Map.StatePlanned") : edge.Removing ? L.Get("Map.StateRemoving")
            : edge.Forward.Managed ? L.Get("Map.StateManaged") : L.Get("Map.StateExternal"), hint: true);

        var route = _map.Ordered(_map.Route(edge));
        AddHeader(L.Get("Map.Route"));
        foreach (var e in route.Where(_map.IsEntry))
            AddLine(L.F("Map.Entry", e.From.Name, e.Forward.ListenPort, FirewallCheck.AccessText(e.Entry, 8)), hint: true);
        foreach (var e in route)
        {
            AddLink($"{e.From.Name}:{e.Forward.ListenPort} → {e.To.Name}:{e.Forward.EffectiveTargetPort}" + PlanMark(e), () => Select(null, e.Key),
                bold: e == edge);
            if (VerdictLine(e) is { } v) AddLine("    " + v, brush: VerdictBrush(e.Verdict!.Kind));
        }
        foreach (var last in route.Where(e => !route.Any(n => n.From.Id == e.To.Server?.Id && ForwardChains.Covers(n.Forward.ListenPort, e.Forward.EffectiveTargetPort))))
            if (last.To.Server?.Facts is { } facts && last.Forward.Protocol != "udp" && int.TryParse(last.Forward.EffectiveTargetPort, out var port))
            {
                var listener = facts.ListeningPorts.FirstOrDefault(l => l.Port == port);
                AddLine(listener != null ? L.F("Map.EndListens", listener.Process ?? "?", last.To.Name, port) : L.F("Map.EndNothing", last.To.Name, port),
                    hint: listener != null, brush: listener == null ? "SystemFillColorCautionBrush" : null);
            }
        if (NetworkMap.Break(route) is { } cut) AddLine(L.F("Map.RouteCut", cut.To.Name), brush: "SystemFillColorCriticalBrush");

        AddHeader("");
        if (edge.Planned != null) AddButton(L.Get("Map.CancelPlanned"), () => CancelPlanned(edge));
        else if (edge.Removing) AddButton(L.Get("Map.KeepForward"), () => ToggleRemove(edge));
        else AddButton(L.Get("Map.RemoveForward"), () => ToggleRemove(edge));
        if (edge.To.Server is { } t) AddButton(L.F("Map.FirewallOf", t.Name), () => OpenFirewall(t));
        AddButton(L.F("Map.ForwardsOf", edge.From.Name), () => OpenForwards(edge.From));
    }

    private static string VerdictBrush(HopKind k) => k switch
    {
        HopKind.Blocked => "SystemFillColorCriticalBrush",
        HopKind.Allowed => "SystemFillColorSuccessBrush",
        HopKind.Unknown => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    private void AddTitle(string text) =>
        Details.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

    private void AddHeader(string text) =>
        Details.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4), TextWrapping = TextWrapping.Wrap });

    private void AddLine(string text, bool hint = false, string? brush = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), FontSize = hint ? 12 : 13 };
        if (hint && brush == null) t.Opacity = 0.7;
        if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Details.Children.Add(t);
    }

    private void AddLink(string text, Action click, bool bold = false)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { TextDecorations = null };
        link.Click += (_, _) => click();
        Details.Children.Add(new TextBlock(link) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
    }

    private void AddButton(string text, Action click)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6), IsEnabled = !_running };
        b.Click += (_, _) => click();
        Details.Children.Add(b);
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();
        void Item(string brush, string text, double[]? dash = null)
        {
            var line = new Line { X1 = 0, Y1 = 7, X2 = 26, Y2 = 7, StrokeThickness = 2, Margin = new Thickness(0, 0, 6, 0) };
            line.SetResourceReference(Shape.StrokeProperty, brush);
            if (dash != null) line.StrokeDashArray = [.. dash];
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            panel.Children.Add(line);
            panel.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.8 });
            Legend.Children.Add(panel);
        }
        Item("TextFillColorSecondaryBrush", L.Get("Map.LegendForward"));
        Item("AccentFillColorDefaultBrush", L.Get("Map.LegendPlanned"), [5, 3]);
        Item("SystemFillColorCriticalBrush", L.Get("Map.LegendRemoving"), [2, 2]);
        if (FirewallBox.IsChecked != true) return;
        Item("SystemFillColorSuccessBrush", L.Get("Map.LegendAllowed"));
        Item("SystemFillColorCriticalBrush", L.Get("Map.LegendBlocked"));
        Item("SystemFillColorCautionBrush", L.Get("Map.LegendUnknown"));
    }

    // ---------- selection ----------

    private void Select(string? node, string? edge)
    {
        _selectedNode = node;
        _selectedEdge = edge;
        Draw();
        ShowDetails();
    }

    private void ScrollToSelection()
    {
        var key = _selectedNode ?? (SelectedEdge is { } edge ? MapNode.KeyOf(edge.From) : null);
        if (key == null || !_pos.ContainsKey(key)) return;
        var r = RectOf(key);
        Scroller.ScrollToHorizontalOffset(Math.Max(0, r.X * Zoom.ScaleX - Scroller.ViewportWidth / 3));
        Scroller.ScrollToVerticalOffset(Math.Max(0, r.Y * Zoom.ScaleY - Scroller.ViewportHeight / 3));
    }

    // ---------- mouse ----------

    private MapTag? TagAt(DependencyObject? d)
    {
        for (var p = d; p != null && p != Board; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is FrameworkElement { Tag: MapTag t }) return t;
        return null;
    }

    private void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        if (_running) return;
        var tag = TagAt(e.OriginalSource as DependencyObject);
        var at = e.GetPosition(Board);
        switch (tag?.Kind)
        {
            case "handle":
                _linkFrom = tag.Key;
                var c = Center(RectOf(tag.Key));
                _rubber = new Line { X1 = RectOf(tag.Key).Right, Y1 = c.Y, X2 = at.X, Y2 = at.Y, StrokeThickness = 2, StrokeDashArray = [5, 3], IsHitTestVisible = false };
                _rubber.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                Panel.SetZIndex(_rubber, 20);
                Board.Children.Add(_rubber);
                break;
            case "node":
                if (_selectedNode != tag.Key || _selectedEdge != null) Select(tag.Key, null);
                _dragNode = tag.Key;
                _dragStart = at;
                _dragOrigin = _pos.TryGetValue(tag.Key, out var p) ? p : new MapPosition();
                _moved = false;
                break;
            case "edge":
                Select(null, tag.Key);
                return;
            default:
                if (_selectedNode != null || _selectedEdge != null) Select(null, null);
                _panStart = e.GetPosition(Scroller);
                _panOffset = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
                break;
        }
        Board.CaptureMouse();
        e.Handled = true;
    }

    private void OnBoardMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(Board);
        if (_rubber != null)
        {
            _rubber.X2 = at.X;
            _rubber.Y2 = at.Y;
        }
        else if (_dragNode != null && _dragOrigin != null)
        {
            var d = at - _dragStart;
            if (!_moved && d.Length < 4) return;
            _moved = true;
            _pos[_dragNode] = new MapPosition { X = Math.Max(0, _dragOrigin.X + d.X), Y = Math.Max(0, _dragOrigin.Y + d.Y) };
            if (_nodeViews.TryGetValue(_dragNode, out var view))
            {
                Canvas.SetLeft(view, _pos[_dragNode].X + Margin0);
                Canvas.SetTop(view, _pos[_dragNode].Y + Margin0);
            }
            DrawEdges(Highlighted());
        }
        else if (_panStart is { } start)
        {
            var now = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(_panOffset.X - (now.X - start.X));
            Scroller.ScrollToVerticalOffset(_panOffset.Y - (now.Y - start.Y));
        }
    }

    private void OnBoardUp(object sender, MouseButtonEventArgs e)
    {
        Board.ReleaseMouseCapture();
        if (_rubber != null)
        {
            Board.Children.Remove(_rubber);
            _rubber = null;
            var from = _linkFrom;
            _linkFrom = null;
            var hit = Board.InputHitTest(e.GetPosition(Board)) as DependencyObject;
            var target = TagAt(hit);
            var targetKey = target?.Kind is "node" or "handle" ? target.Key : null;
            if (from != null && targetKey != from && _map.Node(from) is { } node) OpenLink(node, targetKey == null ? null : _map.Node(targetKey));
        }
        else if (_dragNode != null)
        {
            var key = _dragNode;
            _dragNode = null;
            if (_moved && _pos.TryGetValue(key, out var p))
            {
                _host.Vault.UpdateQuietly(d => d.MapLayout[key] = new MapPosition { X = Math.Round(p.X), Y = Math.Round(p.Y) });
                SizeBoard();
            }
        }
        _panStart = null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var scale = Math.Clamp(Zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 2);
        Zoom.ScaleX = Zoom.ScaleY = scale;
        e.Handled = true;
    }

    private void OnFit(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(Board.Width) || Board.Width <= 0) return;
        var scale = Math.Clamp(Math.Min(Scroller.ViewportWidth / Board.Width, Scroller.ViewportHeight / Board.Height), 0.3, 1.2);
        Zoom.ScaleX = Zoom.ScaleY = scale;
    }

    private void OnAutoLayout(object sender, RoutedEventArgs e)
    {
        var keys = _map.Nodes.Select(n => n.Key).ToHashSet();
        _host.Vault.UpdateQuietly(d =>
        {
            foreach (var k in d.MapLayout.Keys.Where(keys.Contains).ToList()) d.MapLayout.Remove(k);
        });
        Rebuild();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        BuildLegend();
        Rebuild();
    }

    // ---------- context menus ----------

    private ContextMenu NodeMenu(MapNode n)
    {
        var menu = new ContextMenu();
        void Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled && !_running };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        if (n.Server is { } s)
        {
            if (string.IsNullOrWhiteSpace(s.JumpHost)) Add(L.Get("Map.PlanFrom"), () => OpenLink(n, null));
            Add(L.Get("Ctx.PortForwards"), () => OpenForwards(s));
            Add(L.Get("Ctx.Firewall"), () => OpenFirewall(s));
            Add(L.Get("Map.RefreshServer"), () => RefreshServers([s]));
        }
        else Add(L.Get("Map.CopyAddress"), () => Clipboard.SetText(n.Address));
        return menu;
    }

    private ContextMenu EdgeMenu(MapEdge e)
    {
        var menu = new ContextMenu();
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header, IsEnabled = !_running };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add(L.Get("Map.ShowRoute"), () => Select(null, e.Key));
        if (e.Planned != null) Add(L.Get("Map.CancelPlanned"), () => CancelPlanned(e));
        else Add(L.Get(e.Removing ? "Map.KeepForward" : "Map.RemoveForward"), () => ToggleRemove(e));
        if (e.To.Server is { } t) Add(L.F("Map.FirewallOf", t.Name), () => OpenFirewall(t));
        Add(L.F("Map.ForwardsOf", e.From.Name), () => OpenForwards(e.From));
        return menu;
    }

    private void OpenForwards(ServerEntry s) => new PortForwardWindow(_host, s.Clone(), _vm) { Owner = this }.Show();

    private void OpenFirewall(ServerEntry s) => new FirewallWindow(_host, [s.Clone()]) { Owner = this }.Show();

    // ---------- the plan ----------

    private void OpenLink(MapNode from, MapNode? to)
    {
        if (from.Server == null || _running) return;
        var dlg = new MapLinkWindow(_map, _book, from, to) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _plan.Adds.AddRange(dlg.Forwards);
        if (dlg.Allow != null) _plan.Allows.Add(dlg.Allow);
        _selectedNode = null;
        _selectedEdge = "plan:" + dlg.Forwards[0].Id;
        Rebuild();
    }

    private void CancelPlanned(MapEdge e)
    {
        if (e.Planned == null) return;
        _plan.Cancel(e.Planned);
        _selectedEdge = null;
        Rebuild();
    }

    private void ToggleRemove(MapEdge e)
    {
        if (e.Planned != null) return;
        var item = (e.From.Id, e.Forward.Key);
        if (!_plan.Removals.Remove(item)) _plan.Removals.Add(item);
        Rebuild();
    }

    private void UpdatePlanBar()
    {
        var n = _plan.Count;
        PlanText.Text = n == 0 ? "" : L.F("Map.PlanSummary", _plan.Adds.Count, _plan.Removals.Count, _plan.Allows.Count);
        ApplyText.Text = n == 0 ? L.Get("Map.Apply") : L.F("Map.ApplyCount", n);
        ApplyButton.IsEnabled = DiscardButton.IsEnabled = n > 0 && !_running;
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        _plan.Clear();
        _selectedEdge = null;
        Rebuild();
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.Visibility = Visibility.Visible;
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private void SetBusy(bool busy)
    {
        _running = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        RefreshButton.IsEnabled = !busy;
        UpdatePlanBar();
        ShowDetails();
    }

    /// <summary>
    /// Firewall rules first (so that new hops get through when they start), each server keeping its rules only when a
    /// new login works; then the removals, then the new forwards. What fails stays in the plan.
    /// </summary>
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan.Count == 0 || _running) return;
        var lines = new List<string>();
        string Name(Guid id) => _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Name ?? "?");
        foreach (var a in _plan.Allows) lines.Add(L.F("Map.PlanAllowLine", Name(a.ServerId), a.Source));
        foreach (var (id, key) in _plan.Removals) lines.Add(L.F("Map.PlanRemoveLine", Name(id), key));
        foreach (var p in _plan.Adds) lines.Add(L.F("Map.PlanAddLine", Name(p.FromId), p.Protocol, p.ListenPort, p.TargetIp, p.TargetPort.Length == 0 ? p.ListenPort : p.TargetPort));
        if (MessageBox.Show(this, L.F("Map.ConfirmApply", string.Join("\n", lines)), Title, MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        SetBusy(true);
        var allows = _plan.Allows.ToList();
        var removals = _plan.Removals.ToList();
        var adds = _plan.Adds.ToList();
        var done = new List<object>();
        try
        {
            // 1. the rules in the vault (like edits in the firewall window, they are saved at once), then on the servers
            List<ServerEntry> firewalls = [];
            if (allows.Count > 0)
            {
                _host.Vault.Update(d =>
                {
                    foreach (var a in allows)
                        if (d.Servers.FirstOrDefault(s => s.Id == a.ServerId)?.Firewall is { } c && MapPlan.Allow(c, a))
                            done.Add(a);
                });
                var ids = allows.Select(a => a.ServerId).ToHashSet();
                firewalls = _host.Vault.Read(d => d.Servers.Where(s => ids.Contains(s.Id)).Select(s => s.Clone()).ToList());
            }
            await Task.Run(() =>
            {
                if (firewalls.Count > 0) FirewallApplier.Apply(_host, this, firewalls, _clients, Append);
                // 2. removals
                foreach (var (id, key) in removals)
                {
                    var (server, forward) = _host.Vault.Read(d =>
                    {
                        var s = d.Servers.FirstOrDefault(x => x.Id == id);
                        return (s?.Clone(), s?.Facts?.Forwards.FirstOrDefault(f => f.Key == key));
                    });
                    if (server == null || forward == null) continue;
                    try
                    {
                        Append(L.F("Fwd.Deleting", forward.Protocol, forward.ListenPort) + $" ({server.Name})");
                        _host.Forwards.Remove(server, forward, Append);
                        done.Add((id, key));
                    }
                    catch (Exception ex)
                    {
                        Append(L.F("Fw.ResultError", server.Name, ex.Message));
                    }
                }
                // 3. new forwards
                foreach (var p in adds)
                {
                    var server = _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == p.FromId)?.Clone());
                    if (server == null) continue;
                    try
                    {
                        IptablesCommands.Validate(p.Protocol, p.ListenPort, p.TargetIp, p.TargetPort);
                        Append(L.F("Fwd.Adding", p.Protocol, p.ListenPort, p.TargetIp, p.TargetPort.Length == 0 ? p.ListenPort : p.TargetPort) + $" ({server.Name})");
                        _host.Forwards.Add(server, p.Protocol, p.ListenPort, p.TargetIp, p.TargetPort, Append);
                        done.Add(p);
                    }
                    catch (Exception ex)
                    {
                        Append(L.F("Fw.ResultError", server.Name, ex.Message));
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
            foreach (var d in done)
                switch (d)
                {
                    case PlannedAllow a: _plan.Allows.Remove(a); break;
                    case PlannedForward p: _plan.Adds.Remove(p); break;
                    case ValueTuple<Guid, string> r: _plan.Removals.Remove(r); break;
                }
            if (_selectedEdge?.StartsWith("plan:", StringComparison.Ordinal) == true && _plan.Adds.All(p => "plan:" + p.Id != _selectedEdge))
                _selectedEdge = null;
            SetBusy(false);
            Rebuild();
        }
    }

    // ---------- refresh ----------

    private void OnRefreshAll(object sender, RoutedEventArgs e) =>
        RefreshServers(_map.Nodes.Select(n => n.Server).OfType<ServerEntry>().Where(Quaykeep.Core.Inventory.ServerInventoryService.Supported).ToList());

    private async void RefreshServers(List<ServerEntry> servers)
    {
        if (servers.Count == 0) return;
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            Append(L.F("Map.Refreshing", servers.Count));
            var tasks = servers.Select(async s =>
            {
                try
                {
                    await _host.Inventory.RefreshAsync(s.Id);
                }
                catch (Exception ex)
                {
                    Append(L.F("Fw.ResultError", s.Name, ex.Message));
                }
            });
            await Task.WhenAll(tasks);
            Append(L.Get("Addr.Done"));
        }
        finally
        {
            RefreshButton.IsEnabled = !_running;
            if (!_running) Busy.Visibility = Visibility.Hidden;
        }
    }

    // ---------- closing ----------

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            return;
        }
        if (_plan.Count > 0 && MessageBox.Show(this, L.F("Map.ConfirmDiscard", _plan.Count), Title, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Texts the map and the link window share.</summary>
internal static class MapTexts
{
    public static string Verdict(ServerEntry target, ServerEntry from, HopVerdict v, IPAddress? source, string port)
    {
        var who = source == null ? from.Name : $"{from.Name} ({source})";
        var rule = v.Rule == null ? "" : FirewallRules.Describe(v.Rule);
        return v.Kind switch
        {
            HopKind.Open => L.F("Map.VerdictOpen", target.Name, port),
            HopKind.Allowed => L.F("Map.VerdictAllowed", target.Name, who, rule),
            HopKind.Blocked => L.F("Map.VerdictBlocked", target.Name, who, rule),
            _ => L.F("Map.VerdictUnknown", target.Name, from.Name, rule),
        };
    }
}
