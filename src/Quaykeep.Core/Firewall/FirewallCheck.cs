using System.Net;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Firewall;

public enum HopKind
{
    /// <summary>No rule is about this port: Quaykeep's firewall lets everyone through.</summary>
    Open,
    /// <summary>A rule lists the source and lets it through.</summary>
    Allowed,
    /// <summary>A rule drops the source.</summary>
    Blocked,
    /// <summary>Rules decide, but the source address is not known.</summary>
    Unknown,
}

/// <param name="Rule">The rule that decides (null when none does).</param>
public sealed record HopVerdict(HopKind Kind, FirewallRule? Rule);

public enum AccessKind { Everyone, Only, Except }

/// <summary>Who may connect to a port: everyone, only the sources, or everyone except the sources.</summary>
public sealed record PortAccess(AccessKind Kind, List<string> Sources, FirewallRule? Rule);

/// <summary>
/// What Quaykeep's firewall does with a connection, worked out from the rules the way <see cref="FirewallRules.Payload"/>
/// orders them: blocks first, then the first port allow-list about the port, then the whole-server allow-list.
/// </summary>
public static class FirewallCheck
{
    /// <summary>A connection from <paramref name="source"/> (null = not known) to a port ("443" or "1000:2000").</summary>
    public static HopVerdict Check(FirewallConfig config, string protocol, string port, IPAddress? source)
    {
        if (source is { IsIPv4MappedToIPv6: true }) source = source.MapToIPv4();
        var rules = config.Rules.Where(r => r.Enabled && Affects(r, protocol, port)).ToList();
        bool Lists(FirewallRule r) => source != null && r.Sources.Any(s => FirewallRules.TryNetwork(s, out var n) && n.Contains(source));

        var blocks = rules.Where(r => r.Action == FirewallAction.Block).ToList();
        if (blocks.FirstOrDefault(Lists) is { } block) return new HopVerdict(HopKind.Blocked, block);
        if (source == null && blocks.Count > 0) return new HopVerdict(HopKind.Unknown, blocks[0]);
        if (rules.FirstOrDefault(r => r.Action == FirewallAction.AllowOnly && !r.WholeServer) is { } first)
            return new HopVerdict(source == null ? HopKind.Unknown : Lists(first) ? HopKind.Allowed : HopKind.Blocked, first);
        var whole = rules.Where(r => r.Action == FirewallAction.AllowOnly && r.WholeServer).ToList();
        if (whole.Count == 0) return new HopVerdict(HopKind.Open, null);
        if (source == null) return new HopVerdict(HopKind.Unknown, whole[0]);
        return whole.FirstOrDefault(Lists) is { } allow ? new HopVerdict(HopKind.Allowed, allow) : new HopVerdict(HopKind.Blocked, whole[0]);
    }

    /// <summary>Who may connect to the port from anywhere (for the first server of a route).</summary>
    public static PortAccess Access(FirewallConfig config, string protocol, string port)
    {
        var rules = config.Rules.Where(r => r.Enabled && Affects(r, protocol, port)).ToList();
        var blocked = rules.Where(r => r.Action == FirewallAction.Block).SelectMany(r => r.Sources).ToList();
        var allow = rules.FirstOrDefault(r => r.Action == FirewallAction.AllowOnly && !r.WholeServer);
        var whole = rules.Where(r => r.Action == FirewallAction.AllowOnly && r.WholeServer).ToList();
        if (allow != null || whole.Count > 0)
        {
            var sources = allow != null ? allow.Sources : whole.SelectMany(r => r.Sources).ToList();
            if (sources.Any(IsEveryone) && blocked.Count == 0) return new PortAccess(AccessKind.Everyone, [], null); // "+ everyone"
            return new PortAccess(AccessKind.Only, sources.Where(s => !blocked.Contains(s)).Distinct().ToList(), allow ?? whole[0]);
        }
        return blocked.Count > 0
            ? new PortAccess(AccessKind.Except, blocked.Distinct().ToList(), rules.First(r => r.Action == FirewallAction.Block))
            : new PortAccess(AccessKind.Everyone, [], null);
    }

    /// <summary>"everyone", "only 198.51.100.0/24", "everyone except 203.0.113.5".</summary>
    public static string AccessText(PortAccess a, int max = 3)
    {
        var list = string.Join(", ", a.Sources.Take(max)) + (a.Sources.Count > max ? $" +{a.Sources.Count - max}" : "");
        return a.Kind switch
        {
            AccessKind.Only => a.Sources.Count == 0 ? L.Get("Map.AccessNobody") : L.F("Map.AccessOnly", list),
            AccessKind.Except => L.F("Map.AccessExcept", list),
            _ => L.Get("Map.AccessEveryone"),
        };
    }

    private static bool IsEveryone(string s) => FirewallRules.TryNetwork(s, out var n) && n.PrefixLength == 0;

    /// <summary>The rule is about this protocol and port (a whole-server rule is about every port).</summary>
    public static bool Affects(FirewallRule r, string protocol, string port)
    {
        if (r.WholeServer) return true;
        if (protocol != "all" && !r.Protocol.Split(',').Contains(protocol)) return false;
        return FirewallRules.PortItems(r.Ports).Any(p => ForwardChains.Overlaps(p, port));
    }
}
