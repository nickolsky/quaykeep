using System.Net;
using System.Net.Sockets;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Forwarding;

/// <summary>
/// Which server an address belongs to: its host, its public address (GeoIP) and its interfaces' addresses. A private
/// address (10.0.0.2 exists in every cloud) is taken as a server's only when the server that uses it shares that network.
/// </summary>
public sealed class AddressBook
{
    private readonly Dictionary<string, ServerEntry> _public = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(ServerEntry Server, IPNetwork Net)>> _interfaces = new(StringComparer.OrdinalIgnoreCase);

    public AddressBook(IEnumerable<ServerEntry> servers)
    {
        var list = servers.ToList();
        foreach (var s in list)
        {
            if (IPAddress.TryParse(s.Host.Trim().Trim('[', ']'), out var ip)) _public.TryAdd(ip.ToString(), s);
            if (s.Facts?.Geo?.Ip is { Length: > 0 } geo) _public.TryAdd(geo, s);
        }
        foreach (var s in list)
            foreach (var (addr, net) in Interfaces(s))
            {
                if (!IsPrivate(addr)) _public.TryAdd(addr.ToString(), s);
                if (!_interfaces.TryGetValue(addr.ToString(), out var l)) _interfaces[addr.ToString()] = l = [];
                l.Add((s, net));
            }
    }

    /// <summary>
    /// The server with this address, as seen from <paramref name="from"/> (the server that sends there; null = anyone).
    /// A private address counts only when it belongs to one server, and to one on <paramref name="from"/>'s network
    /// when that is known.
    /// </summary>
    public ServerEntry? Find(string ip, ServerEntry? from = null)
    {
        if (_public.TryGetValue(ip, out var s)) return s;
        if (!_interfaces.TryGetValue(ip, out var owners)) return null;
        if (owners.Count != 1) return null;
        // known networks of the sender: the address must be on one of them
        if (from != null && Interfaces(from).Any() && !Interfaces(from).Any(f => f.Net.Contains(IPAddress.Parse(ip)))) return null;
        return owners[0].Server;
    }

    /// <summary>Every address that names a server (for lookups without a sending server).</summary>
    public Dictionary<string, ServerEntry> Map()
    {
        var map = new Dictionary<string, ServerEntry>(_public, StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, owners) in _interfaces)
            if (owners.Count == 1) map.TryAdd(ip, owners[0].Server);
        return map;
    }

    /// <summary>
    /// The address that <paramref name="from"/>'s forwarded traffic comes from at <paramref name="targetIp"/> (forwards
    /// are masqueraded): its own address on the target's network, else its public address; null when not known.
    /// </summary>
    public static IPAddress? SourceToward(ServerEntry from, string targetIp)
    {
        if (!IPAddress.TryParse(targetIp, out var target)) return null;
        foreach (var (addr, net) in Interfaces(from))
            if (addr.AddressFamily == target.AddressFamily && net.Contains(target)) return addr;
        if (IsPrivate(target)) return null; // reached over a network we know nothing about
        return PublicAddress(from);
    }

    /// <summary>Where <paramref name="from"/> should send to reach <paramref name="target"/>: the target's address on a network they share, else its public one.</summary>
    public static string? AddressToward(ServerEntry from, ServerEntry target)
    {
        var mine = Interfaces(from).ToList();
        foreach (var (addr, _) in Interfaces(target))
            if (IsPrivate(addr) && mine.Any(m => m.Net.Contains(addr))) return addr.ToString();
        return PublicAddress(target)?.ToString();
    }

    /// <summary>The address the server is known by on the internet (its host when that is an IP, else GeoIP's).</summary>
    public static IPAddress? PublicAddress(ServerEntry s)
    {
        if (IPAddress.TryParse(s.Host.Trim().Trim('[', ']'), out var ip)) return ip;
        return s.Facts?.Geo?.Ip is { Length: > 0 } geo && IPAddress.TryParse(geo, out var g) ? g : null;
    }

    /// <summary>10/8, 172.16/12, 192.168/16, 100.64/10 (CGNAT), link-local, loopback, IPv6 ULA.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168 ||
               b[0] == 100 && (b[1] & 0xC0) == 64 || b[0] == 169 && b[1] == 254;
    }

    public static bool IsPrivate(string ip) => IPAddress.TryParse(ip, out var a) && IsPrivate(a);

    private static IEnumerable<(IPAddress Addr, IPNetwork Net)> Interfaces(ServerEntry s)
    {
        foreach (var a in s.Facts?.Addresses ?? [])
        {
            var slash = a.IndexOf('/');
            if (IPAddress.TryParse(slash < 0 ? a : a[..slash], out var ip) && FirewallRules.TryNetwork(a, out var net))
                yield return (ip, net);
        }
    }
}
