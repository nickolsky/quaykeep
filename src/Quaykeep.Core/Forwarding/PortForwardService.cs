using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Quaykeep.Core.Models;
using Quaykeep.Core.Inventory;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Forwarding;

public sealed record ForwardChange(List<PortForward> Forwards, string? PersistedWith);

/// <summary>Manages native IPv4 DNAT and cross-family relays (requires root or sudo).</summary>
public sealed class PortForwardService(VaultService vault, SshClientFactory ssh)
{
    public List<PortForward> List(ServerEntry server)
    {
        using var client = Open(server);
        return ReadAndStore(client, server);
    }

    public ForwardChange Add(ServerEntry server, string protocol, string listenPort, string targetIp, string targetPort,
        Action<string> log, string? listenIp = null)
    {
        if (!string.IsNullOrEmpty(listenIp)) return AddRelays(server, [protocol], listenIp, listenPort, targetIp, targetPort, log);
        var script = IptablesCommands.Add(IptablesCommands.NewId(), protocol, listenPort, targetIp, targetPort);
        return Execute(server, script, log, [Validate(protocol, listenPort, targetIp, targetPort)]);
    }

    public ForwardChange AddRelays(ServerEntry server, IReadOnlyList<string> protocols, string listenIp, string listenPort,
        string targetIp, string targetPort, Action<string> log)
    {
        var forwards = protocols.Select(p => RelayCommands.Validate(p, listenIp, listenPort, targetIp, targetPort)).ToList();
        return Execute(server, RelayCommands.Change(null, forwards), log, forwards);
    }

    /// <summary>Changes a forward (removed and added again in one script; the old one comes back when adding fails).</summary>
    public ForwardChange Replace(ServerEntry server, PortForward old, IReadOnlyList<string> protocols, string listenPort, string targetIp,
        string targetPort, Action<string> log, string? listenIp = null)
    {
        var forwards = protocols.Select(p => Validate(p, listenPort, targetIp, targetPort, listenIp)).ToList();
        if (old.IsRelay != !string.IsNullOrEmpty(listenIp))
            return Execute(server, ForwardConversion.Script(old, forwards), log, forwards, old);
        if (old.IsRelay) return Execute(server, RelayCommands.Change(old, forwards), log, forwards);
        var script = IptablesCommands.Replace(old, protocols.Select(p => (IptablesCommands.NewId(), p)).ToList(), listenPort, targetIp, targetPort);
        return Execute(server, script, log, forwards);
    }

    public ForwardChange Remove(ServerEntry server, PortForward forward, Action<string> log) =>
        Execute(server, forward.IsRelay ? RelayCommands.Change(forward, []) : IptablesCommands.Remove(forward), log);

    public ForwardChange InstallPersistence(ServerEntry server, Action<string> log) =>
        Execute(server, IptablesCommands.InstallPersistence, log);

    public static PortForward Validate(string protocol, string listenPort, string targetIp, string targetPort, string? listenIp = null)
    {
        if (!string.IsNullOrEmpty(listenIp)) return RelayCommands.Validate(protocol, listenIp, listenPort, targetIp, targetPort);
        if (IPAddress.TryParse(targetIp, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            throw new ArgumentException(L.Get("Fwd.NeedListenIp"));
        IptablesCommands.Validate(protocol, listenPort, targetIp, targetPort);
        return new PortForward { Protocol = protocol, ListenPort = listenPort, TargetIp = targetIp, TargetPort = targetPort };
    }

    public static string ResolveTarget(string host, string? listenIp)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return host.Contains('%') ? host : literal.ToString();
        var listener = string.IsNullOrEmpty(listenIp) ? IPAddress.Any :
            RelayCommands.ParseAddress(listenIp) ?? throw new ArgumentException(L.F("Fwd.BadListenIp", listenIp));
        var family = !string.IsNullOrEmpty(listenIp) && listener.AddressFamily == AddressFamily.InterNetwork
            ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == family && RelayCommands.ParseAddress(a.ToString()) != null)?.ToString()
            ?? throw new InvalidOperationException(L.F("Fwd.CannotResolve", host));
    }

    private ForwardChange Execute(ServerEntry server, string script, Action<string> log, IReadOnlyList<PortForward>? upcoming = null,
        PortForward? replacing = null)
    {
        log(L.F("Fwd.Connecting", server.Display));
        using var client = Open(server);
        if (upcoming != null)
        {
            var current = Read(client, server);
            if (replacing != null)
            {
                var previous = current.FindIndex(f => replacing.IsRelay ? f.RelayId == replacing.RelayId :
                    !f.IsRelay && f.Rule == replacing.Rule);
                if (previous < 0) throw new InvalidOperationException(L.Get("Fwd.RefreshMissing"));
                current.RemoveAt(previous);
            }
            foreach (var next in upcoming)
                if (current.Any(existing => Shadows(existing, next)))
                    throw new InvalidOperationException(L.F("Fwd.Shadowed", next.Protocol, next.ListenPort));
        }
        var r = RemoteShell.Run(client, server, script, elevated: true, TimeSpan.FromMinutes(5));
        if (RemoteShell.SudoFailed(r)) throw new InvalidOperationException(L.Get("Fwd.NeedRoot") + "\n" + r.Error.Trim());
        if (!r.Output.Split('\n').Any(line => line.TrimEnd('\r') == IptablesCommands.OkMarker))
            throw new InvalidOperationException(L.Get("Fwd.Failed") + "\n" + r.Combined);
        var persisted = IptablesCommands.PersistResult(r.Output);
        log(persisted is null or "none" ? L.Get("Fwd.NotPersisted") : L.F("Fwd.Persisted", persisted));
        var list = ReadAndStore(client, server);
        return new ForwardChange(list, persisted is "none" ? null : persisted);
    }

    public static bool Shadows(PortForward a, PortForward b)
    {
        if (a.IsRelay == b.IsRelay) return false;
        var relay = a.IsRelay ? a : b;
        return !relay.ListenIp!.Contains(':') && (a.Protocol == b.Protocol || a.Protocol == "all" || b.Protocol == "all") &&
               ForwardChains.Overlaps(a.ListenPort, b.ListenPort);
    }

    public static bool Conflicts(PortForward a, PortForward b)
    {
        if (a.IsRelay != b.IsRelay) return Shadows(a, b);
        return (a.Protocol == b.Protocol || a.Protocol == "all" || b.Protocol == "all") &&
               ForwardChains.Overlaps(a.ListenPort, b.ListenPort) &&
               (!a.IsRelay || ForwardChains.AcceptsAddress(a, b.ListenIp!) || ForwardChains.AcceptsAddress(b, a.ListenIp!));
    }

    private SshClient Open(ServerEntry server)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) throw new InvalidOperationException(L.Get("Inventory.JumpHost"));
        return ssh.Connect(server);
    }

    private List<PortForward> ReadAndStore(SshClient client, ServerEntry server)
    {
        var list = Read(client, server);
        vault.UpdateFacts(server.Id, f => f.Forwards = list);
        return list;
    }

    private static List<PortForward> Read(SshClient client, ServerEntry server)
    {
        var r = RemoteShell.Run(client, server, """
            echo '@@sshm:nat'
            if command -v iptables >/dev/null 2>&1; then iptables -t nat -S || exit $?; fi
            """ + "\n" + RelayCommands.Read, elevated: true);
        if (RemoteShell.SudoFailed(r)) throw new InvalidOperationException(L.Get("Fwd.NeedRoot") + "\n" + r.Error.Trim());
        if (!r.Ok) throw new InvalidOperationException(r.Combined);
        var sections = SectionParser.Split(r.Output);
        if (!sections.TryGetValue(RelayCommands.Section, out var relays)) throw new InvalidOperationException(L.Get("Fwd.NeedRoot"));
        var list = IptablesParser.ParseNat(sections.GetValueOrDefault("nat", ""));
        list.AddRange(RelayCommands.Parse(relays));
        return list;
    }
}
