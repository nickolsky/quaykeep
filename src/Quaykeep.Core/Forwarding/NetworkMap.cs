using System.Net;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Forwarding;

/// <summary>A server, or an address that forwards go to and that is not a server in Quaykeep.</summary>
public sealed class MapNode
{
    public required string Key { get; init; }
    public ServerEntry? Server { get; init; }
    public string Address { get; init; } = "";
    /// <summary>For a private address: the server on whose network it is (the same 10.0.0.9 elsewhere is another host).</summary>
    public ServerEntry? Near { get; init; }
    /// <summary>The server's rules with its presets (and the planned changes); null for an address.</summary>
    public FirewallConfig? Firewall { get; init; }
    public FirewallStatus FirewallStatus { get; init; } = FirewallStatus.None;

    public string Name => Server?.Name ?? Address;
    public bool HasRules => Firewall?.Rules.Any(r => r.Enabled) == true;

    public static string KeyOf(ServerEntry s) => s.Id.ToString("N");

    public static string KeyOf(string address, ServerEntry from) =>
        AddressBook.IsPrivate(address) ? $"ip:{from.Id:N}/{address}" : "ip:" + address;
}

/// <summary>A port forward: <see cref="From"/>'s listen port goes to <see cref="To"/>.</summary>
public sealed class MapEdge
{
    public required string Key { get; init; }
    public required ServerEntry From { get; init; }
    public required MapNode To { get; init; }
    public required PortForward Forward { get; init; }
    /// <summary>Drawn on the map, not on the server yet.</summary>
    public PlannedForward? Planned { get; init; }
    /// <summary>On the server, marked for removal.</summary>
    public bool Removing { get; init; }
    /// <summary>What the target's firewall does with the forwarded connections (null when the target is not a server in Quaykeep).</summary>
    public HopVerdict? Verdict { get; init; }
    /// <summary>The address the connections come from at the target (forwards masquerade), null when not known.</summary>
    public IPAddress? Source { get; init; }
    /// <summary>Who may connect to the listen port on <see cref="From"/>.</summary>
    public required PortAccess Entry { get; init; }

    public string Label => $"{Forward.Protocol} {Forward.ListenPort} → {Forward.EffectiveTargetPort}";
}

/// <summary>A forward drawn on the map, to be added on Apply.</summary>
public sealed class PlannedForward
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public Guid FromId { get; init; }
    public string Protocol { get; init; } = "tcp";
    public string ListenPort { get; init; } = "";
    public string TargetIp { get; init; } = "";
    public string TargetPort { get; init; } = "";

    public PortForward ToForward() => new() { Protocol = Protocol, ListenPort = ListenPort, TargetIp = TargetIp, TargetPort = TargetPort };
}

/// <summary>A source to add to one of a server's own firewall rules on Apply (so that a forward into it gets through).</summary>
public sealed class PlannedAllow
{
    public Guid ServerId { get; init; }
    public string RuleId { get; init; } = "";
    public string Source { get; init; } = "";
    /// <summary>The server the address belongs to (the rule then follows its address changes).</summary>
    public Guid? SourceServerId { get; init; }
    /// <summary>The planned forward it is for: removed together with it.</summary>
    public string? ForId { get; init; }
}

/// <summary>Changes drawn on the map and not applied yet.</summary>
public sealed class MapPlan
{
    public List<PlannedForward> Adds { get; } = [];
    /// <summary>Forwards to remove: server and <see cref="PortForward.Key"/>.</summary>
    public List<(Guid ServerId, string ForwardKey)> Removals { get; } = [];
    public List<PlannedAllow> Allows { get; } = [];

    public int Count => Adds.Count + Removals.Count + Allows.Count;

    public void Cancel(PlannedForward p)
    {
        Adds.Remove(p);
        Allows.RemoveAll(a => a.ForId == p.Id);
    }

    public void Clear()
    {
        Adds.Clear();
        Removals.Clear();
        Allows.Clear();
    }

    /// <summary>A server's own firewall rules with the planned sources added.</summary>
    public FirewallConfig? WithAllows(ServerEntry s)
    {
        var mine = Allows.Where(a => a.ServerId == s.Id).ToList();
        if (mine.Count == 0 || s.Firewall == null) return s.Firewall;
        var c = s.Firewall.Clone();
        foreach (var a in mine) Allow(c, a);
        return c;
    }

    /// <summary>Adds the source to the rule (and links it to its server); false when the rule is gone.</summary>
    public static bool Allow(FirewallConfig c, PlannedAllow a)
    {
        var r = c.Rules.FirstOrDefault(r => r.Id == a.RuleId);
        if (r == null) return false;
        if (!r.Sources.Any(s => FirewallRules.TryNetwork(s, out var n) && FirewallRules.TryNetwork(a.Source, out var m) && n.Equals(m)))
            r.Sources.Add(a.Source);
        if (a.SourceServerId is { } id && !r.Links.Any(l => l.ServerId == id && l.Address == a.Source))
            r.Links.Add(new FirewallLink { ServerId = id, Address = a.Source });
        return true;
    }
}

/// <summary>
/// The forwards of all servers as a graph, with what each hop's firewall does to it: forwards masquerade, so the next
/// server sees the previous one's address, and its Quaykeep rules decide whether the hop gets through.
/// </summary>
public sealed class NetworkMap
{
    public const double NodeWidth = 190;
    public const double NodeHeight = 64;
    private const double ColumnStep = 360;
    private const double RowStep = 100;
    private const int IdleColumns = 5;

    public List<MapNode> Nodes { get; } = [];
    public List<MapEdge> Edges { get; } = [];

    /// <param name="onlyLinked">Leave out the servers that no forward starts or ends at.</param>
    public static NetworkMap Build(VaultData data, MapPlan plan, bool onlyLinked = false)
    {
        var map = new NetworkMap();
        var book = new AddressBook(data.Servers);
        var nodes = new Dictionary<string, MapNode>();
        foreach (var s in data.Servers)
        {
            var own = FirewallSets.Effective(s.Firewall, data.FirewallPresets);
            var planned = FirewallSets.Effective(plan.WithAllows(s), data.FirewallPresets);
            nodes[MapNode.KeyOf(s)] = new MapNode
            {
                Key = MapNode.KeyOf(s), Server = s, Firewall = planned, FirewallStatus = FirewallRules.Status(own, s.Facts?.Firewall),
            };
        }

        MapNode Target(ServerEntry from, string ip)
        {
            if (book.Find(ip, from) is { } server) return nodes[MapNode.KeyOf(server)];
            var key = MapNode.KeyOf(ip, from);
            if (!nodes.TryGetValue(key, out var n))
                nodes[key] = n = new MapNode { Key = key, Address = ip, Near = AddressBook.IsPrivate(ip) ? from : null };
            return n;
        }

        MapEdge Edge(ServerEntry from, PortForward f, PlannedForward? planned, bool removing)
        {
            var to = Target(from, f.TargetIp);
            var source = AddressBook.SourceToward(from, f.TargetIp);
            return new MapEdge
            {
                Key = planned != null ? "plan:" + planned.Id : $"{from.Id:N}|{f.Key}",
                From = from, To = to, Forward = f, Planned = planned, Removing = removing, Source = source,
                Verdict = to.Firewall is { } fw ? FirewallCheck.Check(fw, f.Protocol, f.EffectiveTargetPort, source) : null,
                Entry = FirewallCheck.Access(nodes[MapNode.KeyOf(from)].Firewall!, f.Protocol, f.ListenPort),
            };
        }

        foreach (var s in data.Servers)
            foreach (var f in s.Facts?.Forwards ?? [])
                map.Edges.Add(Edge(s, f, null, plan.Removals.Contains((s.Id, f.Key))));
        foreach (var p in plan.Adds)
            if (data.Servers.FirstOrDefault(s => s.Id == p.FromId) is { } from)
                map.Edges.Add(Edge(from, p.ToForward(), p, false));

        var linked = map.Edges.SelectMany(e => new[] { MapNode.KeyOf(e.From), e.To.Key }).ToHashSet();
        map.Nodes.AddRange(nodes.Values.Where(n => n.Server == null || !onlyLinked || linked.Contains(n.Key)));
        return map;
    }

    public MapNode? Node(string key) => Nodes.FirstOrDefault(n => n.Key == key);

    public MapNode? NodeOf(ServerEntry s) => Node(MapNode.KeyOf(s));

    /// <summary>
    /// Every forward on the same route as <paramref name="start"/>: those that bring traffic into its listen port, and
    /// those that take its traffic further, all the way.
    /// </summary>
    public HashSet<MapEdge> Route(MapEdge start)
    {
        var route = new HashSet<MapEdge> { start };
        var down = new Queue<MapEdge>([start]);
        while (down.TryDequeue(out var e))
            foreach (var next in Downstream(e))
                if (route.Add(next)) down.Enqueue(next);
        var up = new Queue<MapEdge>([start]);
        while (up.TryDequeue(out var e))
            foreach (var prev in Upstream(e))
                if (route.Add(prev)) up.Enqueue(prev);
        return route;
    }

    /// <summary>No forward on the map brings traffic into this one's listen port: clients connect to it directly.</summary>
    public bool IsEntry(MapEdge e) => !Upstream(e).Any();

    /// <summary>The route's hops in travel order: where clients come in first, then each next hop.</summary>
    public List<MapEdge> Ordered(HashSet<MapEdge> route)
    {
        var list = new List<MapEdge>();
        var queue = new Queue<MapEdge>(route.Where(e => !Upstream(e).Any(route.Contains)));
        while (queue.TryDequeue(out var e))
        {
            if (list.Contains(e)) continue;
            list.Add(e);
            foreach (var n in Downstream(e).Where(route.Contains)) queue.Enqueue(n);
        }
        list.AddRange(route.Where(e => !list.Contains(e))); // a loop has no start
        return list;
    }

    /// <summary>A hop of the route whose target's firewall drops it.</summary>
    public static MapEdge? Break(IEnumerable<MapEdge> route) =>
        route.Where(e => !e.Removing).FirstOrDefault(e => e.Verdict?.Kind == HopKind.Blocked);

    private IEnumerable<MapEdge> Downstream(MapEdge e) =>
        Edges.Where(n => e.To.Server != null && n.From.Id == e.To.Server.Id && Same(n, e) &&
                         ForwardChains.Covers(n.Forward.ListenPort, e.Forward.EffectiveTargetPort));

    private IEnumerable<MapEdge> Upstream(MapEdge e) =>
        Edges.Where(p => p.To.Server?.Id == e.From.Id && Same(p, e) && ForwardChains.Covers(e.Forward.ListenPort, p.Forward.EffectiveTargetPort));

    private static bool Same(MapEdge a, MapEdge b) =>
        a.Forward.Protocol == b.Forward.Protocol || a.Forward.Protocol == "all" || b.Forward.Protocol == "all";

    /// <summary>
    /// Places the nodes: forwards go left to right, one column per hop, and the servers without forwards go in rows
    /// below. Positions the user saved are kept.
    /// </summary>
    public Dictionary<string, MapPosition> Layout(IReadOnlyDictionary<string, MapPosition>? saved = null)
    {
        var result = new Dictionary<string, MapPosition>();
        var keys = Nodes.Select(n => n.Key).ToHashSet();
        var links = Edges.Select(e => (From: MapNode.KeyOf(e.From), To: e.To.Key)).Where(l => l.From != l.To && keys.Contains(l.From) && keys.Contains(l.To))
            .Distinct().ToList();
        var connected = links.SelectMany(l => new[] { l.From, l.To }).ToHashSet();

        // a column per hop: the longest way in from a node that nothing forwards to (bounded, so cycles end)
        var rank = connected.ToDictionary(k => k, _ => 0);
        for (var pass = 0; pass < connected.Count; pass++)
        {
            var changed = false;
            foreach (var (from, to) in links)
                if (rank[to] < rank[from] + 1 && rank[from] + 1 < connected.Count)
                {
                    rank[to] = rank[from] + 1;
                    changed = true;
                }
            if (!changed) break;
        }
        var names = Nodes.ToDictionary(n => n.Key, n => n.Name);
        var row = new Dictionary<string, double>();
        var bottom = -RowStep;
        foreach (var column in rank.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        {
            // next to the nodes that forward into them
            var ordered = column.Select(kv => kv.Key).OrderBy(k =>
            {
                var from = links.Where(l => l.To == k && row.ContainsKey(l.From)).Select(l => row[l.From]).ToList();
                return from.Count == 0 ? double.MaxValue : from.Average();
            }).ThenBy(k => names[k], StringComparer.CurrentCultureIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                row[ordered[i]] = i;
                result[ordered[i]] = new MapPosition { X = column.Key * ColumnStep, Y = i * RowStep };
                bottom = Math.Max(bottom, i * RowStep);
            }
        }
        var idle = Nodes.Where(n => !connected.Contains(n.Key)).OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var top = bottom + RowStep * 1.6;
        for (var i = 0; i < idle.Count; i++)
            result[idle[i].Key] = new MapPosition { X = i % IdleColumns * (NodeWidth + 40), Y = top + i / IdleColumns * RowStep };

        if (saved != null)
            foreach (var k in result.Keys.ToList())
                if (saved.TryGetValue(k, out var p)) result[k] = new MapPosition { X = p.X, Y = p.Y };
        return result;
    }
}
