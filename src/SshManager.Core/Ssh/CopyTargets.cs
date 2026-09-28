using System.Net;
using System.Net.Sockets;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;

namespace SshManager.Core.Ssh;

public enum CopyKind { HostName, Ip, Password, PublicKey, SshCommand, Url }

/// <param name="Url">For <see cref="CopyKind.Url"/>: the address to copy.</param>
public sealed record CopyTarget(CopyKind Kind, string? Url = null);

/// <summary>What "Copy ▸" in a server's menu offers, and the DNS lookups behind the host name / IP entries.</summary>
public static class CopyTargets
{
    private static readonly (int Port, string Scheme)[] WebPorts = [(80, "http"), (443, "https"), (8080, "http"), (8443, "https")];

    /// <param name="hasKey">The server's key is in the vault (its public key can be copied).</param>
    /// <param name="ports">Monitored port states from the health monitor.</param>
    public static IReadOnlyList<CopyTarget> For(ServerEntry server, bool hasKey, IReadOnlyDictionary<int, ServerHealth> ports)
    {
        var list = new List<CopyTarget> { new(CopyKind.HostName), new(CopyKind.Ip) };
        if (!string.IsNullOrEmpty(server.Password)) list.Add(new(CopyKind.Password));
        if (hasKey) list.Add(new(CopyKind.PublicKey));
        list.Add(new(CopyKind.SshCommand));
        foreach (var (port, scheme) in WebPorts)
            if (IsOpen(server, port, ports)) list.Add(new(CopyKind.Url, WebUrl(server.Host, port, scheme)));
        return list;
    }

    /// <summary>
    /// A monitored port's last check decides; otherwise the port counts as open when the server listens on it
    /// on a non-loopback address (from the last inventory).
    /// </summary>
    public static bool IsOpen(ServerEntry server, int port, IReadOnlyDictionary<int, ServerHealth> ports)
    {
        if (ports.TryGetValue(port, out var h) && h.State is HealthState.Online or HealthState.Offline)
            return h.State == HealthState.Online;
        return server.Facts?.ListeningPorts.Any(p => p.Port == port && !p.LocalOnly) == true;
    }

    public static bool IsIp(string host) => IPAddress.TryParse(host.Trim().Trim('[', ']'), out _);

    /// <summary>http://host or https://host:8443; the default port of the scheme is left out, IPv6 goes in brackets.</summary>
    public static string WebUrl(string host, int port, string scheme)
    {
        host = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6) host = $"[{host}]";
        var isDefault = (scheme == "http" && port == 80) || (scheme == "https" && port == 443);
        return isDefault ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
    }

    /// <summary>The host itself when it is an IP; otherwise its DNS address from this PC, IPv4 first.</summary>
    public static async Task<string> ResolveIpAsync(string host, CancellationToken ct = default) =>
        (await PingService.ResolveAsync(host, ct)).ToString();

    /// <summary>The host itself when it is a name; for an IP, its reverse DNS name, or null when it has none.</summary>
    public static async Task<string?> ResolveHostNameAsync(string host, CancellationToken ct = default)
    {
        host = host.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(host, out var ip)) return host;
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct);
            return string.IsNullOrEmpty(entry.HostName) || entry.HostName == ip.ToString() ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
