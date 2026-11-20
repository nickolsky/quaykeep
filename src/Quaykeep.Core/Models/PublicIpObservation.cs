using System.Net;
using System.Net.Sockets;

namespace Quaykeep.Core.Models;

public enum PublicIpCheckIssue { None, HostKey, Ssh, CurlMissing, Incomplete }

/// <summary>Last observed address for each family, with success flags for the latest check.</summary>
public sealed record PublicIpObservation(DateTime CheckedUtc, string? Ipv4, string? Ipv6, bool Ipv4Current, bool Ipv6Current)
{
    public const int DefaultIntervalMinutes = 24 * 60;
    public PublicIpCheckIssue Issue { get; init; }
    public int? Ipv4CurlExitCode { get; init; }
    public int? Ipv6CurlExitCode { get; init; }

    public bool Due(DateTime now, int minutes) => now - CheckedUtc >=
        TimeSpan.FromMinutes(minutes > 0 ? minutes : DefaultIntervalMinutes);

    public static string? Normalize(string? address, AddressFamily family)
    {
        address = address?.Trim();
        if (string.IsNullOrEmpty(address) || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != family)
            return null;
        if (family == AddressFamily.InterNetwork && address != ip.ToString()) return null;
        return ip.IsIPv4MappedToIPv6 || address.Contains('%') ? null : ip.ToString();
    }

    public static PublicIpObservation Observe(DateTime now, PublicIpObservation? previous, string? ipv4, string? ipv6) =>
        new(now, ipv4 ?? previous?.Ipv4, ipv6 ?? previous?.Ipv6, ipv4 != null, ipv6 != null);
}
