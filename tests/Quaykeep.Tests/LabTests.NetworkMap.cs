using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Inventory;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;

namespace Quaykeep.Tests;

public partial class LabTests
{
    /// <summary>
    /// The inventory reads each server's interface addresses; on the map, a forward to another lab server's private
    /// address (the lab's Docker network) then ends at that server, and the hop is checked against its firewall with
    /// the sender's address on that network.
    /// </summary>
    [Fact]
    public async Task Network_Map_Finds_Forward_Targets_By_Their_Private_Address()
    {
        if (!Enabled("netmap")) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var ssh = new SshClientFactory(vault, new KnownHostsService(tmp.File("known_hosts"))) { ConfirmHostKey = _ => true };
        var labs = Targets().Select(t => new ServerEntry { Name = "lab-" + t.Name, Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password })
            .ToList();
        vault.Update(d => d.Servers.AddRange(labs));
        foreach (var s in labs) ssh.Connect(s).Dispose(); // trusts the host keys (the inventory does not ask)
        var inventory = new ServerInventoryService(vault, ssh);
        await Task.WhenAll(labs.Select(s => inventory.RefreshAsync(s.Id)));

        var data = vault.Data;
        foreach (var s in data.Servers)
        {
            var facts = s.Facts!;
            Assert.True(facts.InventoryError == null, $"{s.Name}: {facts.InventoryError}");
            log.WriteLine($"{s.Name}: {string.Join(", ", facts.Addresses)}");
            var own = Sh(new Lab(s, ssh, null!, vault), "ip -4 -o addr show scope global | awk '{print $4}' | head -1").Trim();
            Assert.Contains(own, facts.Addresses);
        }
        if (data.Servers.Count < 2) return;

        // the first server forwards 8080 to the second one's address on their network
        var (a, b) = (data.Servers[0], data.Servers[1]);
        var target = AddressBook.AddressToward(a, b);
        Assert.NotNull(target);
        Assert.True(AddressBook.IsPrivate(target!), target);
        a.Facts!.Forwards = [new PortForward { Protocol = "tcp", ListenPort = "8080", TargetIp = target!, TargetPort = "80" }];
        var source = AddressBook.SourceToward(a, target!);
        Assert.NotNull(source);
        b.Firewall = new FirewallConfig { Rules = [new FirewallRule { Action = FirewallAction.AllowOnly, Ports = "80", Sources = [source!.ToString()] }] };

        var map = NetworkMap.Build(data, new MapPlan());
        var edge = Assert.Single(map.Edges);
        Assert.Same(b, edge.To.Server);
        Assert.Equal(HopKind.Allowed, edge.Verdict!.Kind);
        b.Firewall.Rules[0].Sources = ["192.0.2.1"];
        Assert.Equal(HopKind.Blocked, Assert.Single(NetworkMap.Build(data, new MapPlan()).Edges).Verdict!.Kind);
    }
}
