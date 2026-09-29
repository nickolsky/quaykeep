using System.Net;
using System.Windows;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>
/// Applies what the vault holds for each server (its own rules and its presets), one server after another, from a
/// window's background task. Asks on the window's thread before a set that would refuse SSH from this PC; each server
/// keeps its new rules only when a new login to it works (<see cref="FirewallService.Apply"/>).
/// </summary>
internal static class FirewallApplier
{
    /// <param name="clients">This PC's address per server, filled in as it is learned.</param>
    public static void Apply(AppHost host, Window owner, IEnumerable<ServerEntry> servers, Dictionary<Guid, IPAddress> clients,
        Action<string> log)
    {
        foreach (var server in servers)
        {
            try
            {
                var effective = host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == server.Id) is { } s ? FirewallSets.Effective(s, d) : null);
                if (effective == null) continue;
                FirewallRules.Validate(effective);
                IPAddress? me;
                lock (clients) clients.TryGetValue(server.Id, out me);
                if (me == null && host.Firewall.ClientAddress(server) is { } found)
                {
                    me = found;
                    lock (clients) clients[server.Id] = found;
                }
                if (me != null && FirewallRules.CutsSsh(effective, server.Port, me) &&
                    !owner.Dispatcher.Invoke(() => MessageBox.Show(owner, L.F("Fw.ConfirmCutsSsh", server.Name, me), owner.Title,
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK))
                {
                    log(L.F("Fw.Skipped", server.Name));
                    continue;
                }
                log(L.F("Fw.Applying", server.Name));
                var r = host.Firewall.Apply(server, effective, log);
                log(L.F(r.Applied ? "Fw.ResultApplied" : "Fw.ResultReverted", server.Name));
            }
            catch (Exception ex)
            {
                log(L.F("Fw.ResultError", server.Name, ex.Message));
            }
        }
    }

    /// <summary>A server's IP for a rule: the host itself, its last resolved address, or a DNS lookup now.</summary>
    public static async Task<string?> AddressOf(ServerEntry s)
    {
        if (IPAddress.TryParse(s.Host.Trim().Trim('[', ']'), out var ip)) return ip.ToString();
        if (s.Facts?.Geo?.Ip is { Length: > 0 } known) return known;
        return await Quaykeep.Core.Geo.GeoIpService.ResolveAsync(s.Host);
    }
}
