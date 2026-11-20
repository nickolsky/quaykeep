using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Core.Forwarding;

/// <summary>Configuration and lifecycle for Quaykeep's persistent cross-family TCP and UDP relays.</summary>
public static class RelayCommands
{
    internal const string Section = "relays";
    private static readonly Lazy<string> Program = new(() => Resource("relay.py"));
    private static readonly Lazy<string> Manage = new(() => Resource("relay-manage.sh"));

    private const string Unit = """
        # Managed by Quaykeep cross-family forwarding
        [Unit]
        Description=Quaykeep cross-family port forward %i
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=notify
        ExecStart=/usr/bin/python3 /etc/quaykeep/relays/relay.py /etc/quaykeep/relays/%i.conf
        DynamicUser=yes
        NoNewPrivileges=yes
        AmbientCapabilities=CAP_NET_BIND_SERVICE
        CapabilityBoundingSet=CAP_NET_BIND_SERVICE
        RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
        ProtectSystem=strict
        ProtectHome=yes
        TimeoutStartSec=12s
        TimeoutStopSec=5s
        Restart=on-failure
        RestartSec=2s

        [Install]
        WantedBy=multi-user.target
        """;

    internal static string Read => """
        if [ "$(id -u)" = 0 ]; then
          echo '@@sshm:relays'
          for config in /etc/quaykeep/relays/*.conf; do
            [ -f "$config" ] && [ ! -L "$config" ] || continue
            id=${config##*/}; id=${id%.conf}
            [ -n "$id" ] || continue
            printf '%s ' "$id"
            cat -- "$config"
          done
        fi
        """.Replace("\r\n", "\n");

    public static IPAddress? ParseAddress(string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (value.Contains('%') || !IPAddress.TryParse(value, out var address)) return null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv4MappedToIPv6 || address.IsIPv6LinkLocal || address.IsIPv6Multicast ? null : address;
        return address.GetAddressBytes()[0] >= 224 ? null : address;
    }

    public static PortForward Validate(string protocol, string listenIp, string listenPort, string targetIp, string targetPort)
    {
        if (protocol is not ("tcp" or "udp")) throw new ArgumentException(L.Get("Fwd.BadProtocol"));
        var listener = ParseAddress(listenIp) ?? throw new ArgumentException(L.F("Fwd.BadListenIp", listenIp));
        var target = ParseAddress(targetIp);
        if (target == null || target.Equals(IPAddress.Any) || target.Equals(IPAddress.IPv6Any))
            throw new ArgumentException(L.F("Fwd.BadRelayTarget", targetIp));
        if (target.AddressFamily == listener.AddressFamily)
            throw new ArgumentException(L.Get("Fwd.DifferentFamilies"));
        return new PortForward
        {
            Protocol = protocol, ListenIp = listener.ToString(), ListenPort = SinglePort(listenPort),
            TargetIp = target.ToString(), TargetPort = SinglePort(string.IsNullOrEmpty(targetPort) ? listenPort : targetPort),
        };
    }

    private static string SinglePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException(L.F("Fwd.BadRelayPort", value));

    internal static List<PortForward> Parse(string text)
    {
        var forwards = new List<PortForward>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6 || !ValidId(fields[0])) continue;
            try
            {
                var forward = Validate(fields[1], fields[2], fields[3], fields[4], fields[5]);
                forward.RelayId = fields[0];
                forwards.Add(forward);
            }
            catch (ArgumentException) { } // an incomplete or invalid file is never presented as a managed relay
        }
        return forwards;
    }

    internal static string Change(PortForward? old, IReadOnlyList<PortForward> forwards)
    {
        if (old != null && (old.RelayId == null || !ValidId(old.RelayId))) throw new ArgumentException(L.Get("Fwd.BadRelayId"));
        var lines = forwards.Select(f =>
        {
            var rule = Validate(f.Protocol, f.ListenIp!, f.ListenPort, f.TargetIp, f.TargetPort);
            var id = f.RelayId ?? Guid.NewGuid().ToString("N");
            if (!ValidId(id)) throw new ArgumentException(L.Get("Fwd.BadRelayId"));
            return $"{id} {rule.Protocol} {rule.ListenIp} {rule.ListenPort} {rule.TargetIp} {rule.TargetPort}";
        });
        return $"action={(forwards.Count == 0 ? "remove" : "apply")}\nold={RemoteShell.Quote(old?.RelayId ?? "")}\n" +
               (forwards.Count == 0 ? "" : $"definitions={RemoteShell.Quote(string.Join('\n', lines))}\nprogram={RemoteShell.Quote(Program.Value.TrimEnd('\n'))}\n" +
                $"template={RemoteShell.Quote(Unit.Replace("\r\n", "\n").TrimEnd('\n'))}\n") + Manage.Value;
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Resource(string name)
    {
        using var stream = typeof(RelayCommands).Assembly.GetManifestResourceStream("Quaykeep.Core.Forwarding." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
