using Quaykeep.Core.Models;

namespace Quaykeep.Core.Firewall;

/// <summary>Where a server's address is used: a firewall rule (of a server or of a preset) or a port forward.</summary>
/// <param name="ServerId">The server whose own rule it is (null for a preset's rule).</param>
/// <param name="Linked">The address was added as that server's address (not just typed in).</param>
public sealed record FirewallUse(Guid? ServerId, Guid? PresetId, FirewallRule Rule, bool Linked);

/// <summary>A server's rules together with its presets, and following a server's address into rules and forwards.</summary>
public static class FirewallSets
{
    /// <summary>What goes to the server: its own rules, then the rules of its presets in the order they are ticked.</summary>
    public static FirewallConfig Effective(FirewallConfig? own, IReadOnlyList<FirewallPreset> presets)
    {
        var c = new FirewallConfig();
        if (own == null) return c;
        c.Rules.AddRange(own.Rules.Select(r => r.Clone()));
        foreach (var id in own.Presets)
            if (presets.FirstOrDefault(p => p.Id == id) is { } p) c.Rules.AddRange(p.Rules.Select(r => r.Clone()));
        return c;
    }

    public static FirewallConfig Effective(ServerEntry server, VaultData data) => Effective(server.Firewall, data.FirewallPresets);

    public static List<ServerEntry> UsingPreset(VaultData data, Guid presetId) =>
        data.Servers.Where(s => s.Firewall?.Presets.Contains(presetId) == true).ToList();

    /// <summary>Keeps the links whose address is still one of the rule's sources.</summary>
    public static void PruneLinks(FirewallRule r)
    {
        var sources = r.Sources.Select(Key).ToHashSet();
        r.Links.RemoveAll(l => !sources.Contains(Key(l.Address)));
        r.Links = r.Links.DistinctBy(l => Key(l.Address)).ToList();
    }

    /// <summary>Rules that name <paramref name="oldAddress"/>: linked to the server, or the same address typed in.</summary>
    public static List<FirewallUse> UsesOf(VaultData data, Guid serverId, string oldAddress)
    {
        var key = Key(oldAddress);
        var list = new List<FirewallUse>();
        void Scan(Guid? server, Guid? preset, IEnumerable<FirewallRule> rules)
        {
            foreach (var r in rules)
            {
                var linked = r.Links.Any(l => l.ServerId == serverId && Key(l.Address) == key);
                if (linked || r.Sources.Any(s => Key(s) == key)) list.Add(new FirewallUse(server, preset, r, linked));
            }
        }
        foreach (var s in data.Servers.Where(s => s.Firewall != null)) Scan(s.Id, null, s.Firewall!.Rules);
        foreach (var p in data.FirewallPresets) Scan(null, p.Id, p.Rules);
        return list;
    }

    /// <summary>Puts <paramref name="newAddress"/> in place of the old one and links it to the server.</summary>
    public static void Replace(FirewallRule r, Guid serverId, string oldAddress, string newAddress)
    {
        var key = Key(oldAddress);
        var sources = r.Sources.Select(s => Key(s) == key ? newAddress : s).ToList();
        r.Sources = sources.DistinctBy(Key).ToList();
        r.Links.RemoveAll(l => Key(l.Address) == key);
        r.Links.Add(new FirewallLink { ServerId = serverId, Address = newAddress });
        PruneLinks(r);
    }

    /// <summary>Port forwards on any server that send traffic to <paramref name="oldAddress"/>.</summary>
    public static List<(ServerEntry Server, PortForward Forward)> ForwardsTo(VaultData data, string oldAddress) =>
        data.Servers.SelectMany(s => (s.Facts?.Forwards ?? []).Where(f => f.TargetIp == oldAddress).Select(f => (s, f))).ToList();

    /// <summary>"1.2.3.4" and "1.2.3.4/32" are the same source.</summary>
    private static string Key(string source) => FirewallRules.TryNetwork(source, out var n) ? n.ToString() : source.Trim().ToLowerInvariant();
}
