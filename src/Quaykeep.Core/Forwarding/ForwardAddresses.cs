using System.Net;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Forwarding;

public enum ForwardAddressSource { Ssh, Interface, PublicOutbound, LastKnownPublicOutbound }

public sealed record ForwardAddress(string Address, ForwardAddressSource Source);

public static class ForwardAddresses
{
    /// <summary>Bind to a wildcard or an address reported on the forwarding server's own interfaces.</summary>
    public static IEnumerable<string> Listen(ServerEntry server) =>
        new[] { IPAddress.Any.ToString(), IPAddress.IPv6Any.ToString() }.Concat(InterfaceAddresses(server)).Distinct();

    public static IReadOnlyList<ForwardAddress> Targets(ServerEntry server)
    {
        var choices = new List<ForwardAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, ForwardAddressSource source, bool allowHost = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            if (IPAddress.TryParse(value, out _))
            {
                var ip = RelayCommands.ParseAddress(value);
                if (ip == null || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return;
                value = ip.ToString();
            }
            else if (!allowHost) return;
            if (seen.Add(value)) choices.Add(new ForwardAddress(value, source));
        }

        if (!server.Ssm)
        {
            Add(server.Host, ForwardAddressSource.Ssh, allowHost: true);
            Add(AddressBook.PublicAddress(server)?.ToString(), ForwardAddressSource.Ssh);
        }
        foreach (var address in InterfaceAddresses(server)) Add(address, ForwardAddressSource.Interface);
        var observation = server.AutoDetectPublicIps ? server.Facts?.PublicIps : null;
        Add(server.PublicIpv4, observation?.Ipv4Current == false ? ForwardAddressSource.LastKnownPublicOutbound : ForwardAddressSource.PublicOutbound);
        Add(server.PublicIpv6, observation?.Ipv6Current == false ? ForwardAddressSource.LastKnownPublicOutbound : ForwardAddressSource.PublicOutbound);
        return choices;
    }

    public static IEnumerable<string> InterfaceAddresses(ServerEntry server) =>
        (server.Facts?.Addresses ?? []).Select(a => a.Split('/')[0])
            .Select(RelayCommands.ParseAddress).OfType<IPAddress>().Select(a => a.ToString());
}
