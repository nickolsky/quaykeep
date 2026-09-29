using System.Net;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Inventory;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public class NetworkMapTests
{
    private static ServerEntry Server(string name, string host, params string[] addresses) =>
        new() { Name = name, Host = host, Facts = new ServerFacts { Addresses = [.. addresses] } };

    private static PortForward Fwd(string listen, string target, string port = "") =>
        new() { Protocol = "tcp", ListenPort = listen, TargetIp = target, TargetPort = port };

    private static FirewallRule AllowOnly(string ports, params string[] sources) =>
        new() { Action = FirewallAction.AllowOnly, Ports = ports, Protocol = "tcp", Sources = [.. sources] };

    [Fact]
    public void Interface_Addresses_Leave_Out_Loopback_Docker_And_Link_Local()
    {
        const string text = """
            1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
            2: eth0    inet 203.0.113.10/24 brd 203.0.113.255 scope global eth0\       valid_lft forever preferred_lft forever
            2: eth0    inet6 fe80::1/64 scope link \       valid_lft forever preferred_lft forever
            3: ens7    inet 10.0.0.5/24 brd 10.0.0.255 scope global dynamic ens7\       valid_lft 3000sec preferred_lft 3000sec
            4: docker0    inet 172.17.0.1/16 brd 172.17.255.255 scope global docker0\       valid_lft forever preferred_lft forever
            5: br-1a2b    inet 172.18.0.1/16 scope global br-1a2b\       valid_lft forever preferred_lft forever
            6: eth1@if12    inet6 2001:db8::5/64 scope global \       valid_lft forever preferred_lft forever
            """;
        Assert.Equal(["203.0.113.10/24", "10.0.0.5/24", "2001:db8::5/64"], InterfaceAddressParser.Parse(text));
    }

    [Fact]
    public void Private_Addresses_Belong_To_A_Server_Only_On_A_Shared_Network()
    {
        var a = Server("a", "203.0.113.1", "203.0.113.1/24", "10.0.0.5/24");
        var b = Server("b", "b.example.com", "10.0.0.6/24");
        var far = Server("far", "198.51.100.1", "192.168.1.2/24");
        var twin1 = Server("t1", "198.51.100.2", "10.9.0.1/24");
        var twin2 = Server("t2", "198.51.100.3", "10.9.0.1/24");
        var book = new AddressBook([a, b, far, twin1, twin2]);

        Assert.Same(b, book.Find("10.0.0.6", a));
        Assert.Null(book.Find("10.0.0.6", far)); // the same address on another network is another host
        Assert.Same(b, book.Find("10.0.0.6")); // nobody asking: it is the only one with it
        Assert.Null(book.Find("10.9.0.1")); // two servers have it
        Assert.Same(a, book.Find("203.0.113.1"));

        Assert.Equal(IPAddress.Parse("10.0.0.5"), AddressBook.SourceToward(a, "10.0.0.6"));
        Assert.Equal(IPAddress.Parse("203.0.113.1"), AddressBook.SourceToward(a, "198.51.100.1"));
        Assert.Null(AddressBook.SourceToward(far, "10.0.0.6")); // some private network we know nothing about
        Assert.Equal("10.0.0.6", AddressBook.AddressToward(a, b));
        Assert.Equal("198.51.100.1", AddressBook.AddressToward(a, far));
    }

    [Fact]
    public void Firewall_Check_Follows_The_Rule_Order()
    {
        var src = IPAddress.Parse("198.51.100.7");
        var none = new FirewallConfig();
        Assert.Equal(HopKind.Open, FirewallCheck.Check(none, "tcp", "443", src).Kind);

        var c = new FirewallConfig
        {
            Rules =
            [
                new FirewallRule { Action = FirewallAction.Block, Ports = "8080", Protocol = "tcp", Sources = ["198.51.100.0/24"] },
                AllowOnly("443", "203.0.113.0/24"),
                AllowOnly("443", "198.51.100.7"), // a second list for the port: the first one decides
                new FirewallRule { Action = FirewallAction.AllowOnly, Sources = ["198.51.100.7"] }, // whole server
            ],
        };
        Assert.Equal(HopKind.Blocked, FirewallCheck.Check(c, "tcp", "8080", src).Kind);
        Assert.Same(c.Rules[1], FirewallCheck.Check(c, "tcp", "443", src).Rule);
        Assert.Equal(HopKind.Blocked, FirewallCheck.Check(c, "tcp", "443", src).Kind);
        Assert.Equal(HopKind.Allowed, FirewallCheck.Check(c, "tcp", "443", IPAddress.Parse("203.0.113.9")).Kind);
        Assert.Equal(HopKind.Allowed, FirewallCheck.Check(c, "tcp", "22", src).Kind); // only the whole-server list is about 22
        Assert.Equal(HopKind.Blocked, FirewallCheck.Check(c, "tcp", "22", IPAddress.Parse("192.0.2.1")).Kind);
        Assert.Equal(HopKind.Unknown, FirewallCheck.Check(c, "tcp", "443", null).Kind);
        Assert.Equal(HopKind.Allowed, FirewallCheck.Check(c, "udp", "443", src).Kind); // tcp rules; the whole-server one lists it
        Assert.Equal(HopKind.Blocked, FirewallCheck.Check(c, "tcp", "8000:8100", src).Kind); // a range with 8080 in it

        Assert.Equal(AccessKind.Only, FirewallCheck.Access(c, "tcp", "443").Kind);
        Assert.Equal(["203.0.113.0/24"], FirewallCheck.Access(c, "tcp", "443").Sources);
        var blocks = new FirewallConfig { Rules = [c.Rules[0]] };
        Assert.Equal(AccessKind.Except, FirewallCheck.Access(blocks, "tcp", "8080").Kind);
        Assert.Equal(AccessKind.Everyone, FirewallCheck.Access(blocks, "tcp", "443").Kind);
    }

    /// <summary>edge:443 → mid:8443 → end:443, mid lets only edge's address in on 8443, end lets nobody in on 443.</summary>
    private static (VaultData Data, ServerEntry Edge, ServerEntry Mid, ServerEntry End) Chain()
    {
        var edge = Server("edge", "203.0.113.1", "203.0.113.1/24");
        var mid = Server("mid", "198.51.100.2", "198.51.100.2/24", "10.0.0.2/24");
        var end = Server("end", "192.0.2.3", "10.0.0.3/24");
        edge.Facts!.Forwards = [Fwd("443", "198.51.100.2", "8443")];
        mid.Facts!.Forwards = [Fwd("8443", "10.0.0.3", "443")];
        end.Facts!.ListeningPorts = [new ListeningPort { Port = 443, Process = "nginx" }];
        mid.Firewall = new FirewallConfig { Rules = [AllowOnly("8443", "203.0.113.1")] };
        end.Firewall = new FirewallConfig { Rules = [AllowOnly("443", "192.0.2.200")] };
        var idle = Server("idle", "192.0.2.9");
        return (new VaultData { Servers = [edge, mid, end, idle] }, edge, mid, end);
    }

    [Fact]
    public void A_Chain_Shows_Each_Hops_Firewall_And_Its_Whole_Route()
    {
        var (data, edge, mid, end) = Chain();
        var map = NetworkMap.Build(data, new MapPlan());
        Assert.Equal(4, map.Nodes.Count);
        var first = map.Edges.Single(e => e.From == edge);
        var second = map.Edges.Single(e => e.From == mid);
        Assert.Same(mid, first.To.Server);
        Assert.Same(end, second.To.Server); // a private address on mid's network
        Assert.Equal(HopKind.Allowed, first.Verdict!.Kind);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), second.Source); // masqueraded: end sees mid's address on their network
        Assert.Equal(HopKind.Blocked, second.Verdict!.Kind);
        Assert.Equal(FirewallStatus.Unknown, map.NodeOf(mid)!.FirewallStatus);

        Assert.Equal([first, second], map.Ordered(map.Route(second)));
        Assert.True(map.IsEntry(first));
        Assert.False(map.IsEntry(second));
        Assert.Same(second, NetworkMap.Break(map.Route(first)));
        Assert.Equal(AccessKind.Everyone, first.Entry.Kind);
        Assert.Equal(AccessKind.Only, second.Entry.Kind); // who may reach mid:8443 at all

        Assert.Equal(3, NetworkMap.Build(data, new MapPlan(), onlyLinked: true).Nodes.Count);
        var layout = map.Layout();
        Assert.True(layout[MapNode.KeyOf(edge)].X < layout[MapNode.KeyOf(mid)].X);
        Assert.True(layout[MapNode.KeyOf(mid)].X < layout[MapNode.KeyOf(end)].X);
        Assert.True(layout[map.Nodes.Single(n => n.Name == "idle").Key].Y > layout[MapNode.KeyOf(end)].Y); // below the routes
        var saved = new Dictionary<string, MapPosition> { [MapNode.KeyOf(end)] = new() { X = 5, Y = 7 } };
        Assert.Equal(7, map.Layout(saved)[MapNode.KeyOf(end)].Y);
    }

    [Fact]
    public void Planned_Forwards_Removals_And_Allows_Show_Before_Apply()
    {
        var (data, edge, mid, end) = Chain();
        var plan = new MapPlan();
        var planned = new PlannedForward { FromId = edge.Id, Protocol = "tcp", ListenPort = "80", TargetIp = "10.0.0.3" };
        plan.Adds.Add(planned);
        plan.Removals.Add((mid.Id, mid.Facts!.Forwards[0].Key));
        plan.Allows.Add(new PlannedAllow { ServerId = end.Id, RuleId = end.Firewall!.Rules[0].Id, Source = "10.0.0.2", SourceServerId = mid.Id, ForId = planned.Id });

        var map = NetworkMap.Build(data, plan);
        var added = map.Edges.Single(e => e.Planned == planned);
        Assert.Equal("10.0.0.3", added.To.Address); // edge is not on end's network: just an address
        Assert.Null(added.To.Server);
        Assert.True(map.Edges.Single(e => e.From == mid).Removing);
        Assert.Equal(HopKind.Allowed, map.Edges.Single(e => e.From == mid).Verdict!.Kind); // with the planned source
        Assert.Single(end.Firewall.Rules[0].Sources); // the vault is not touched until Apply

        var rules = end.Firewall.Clone();
        Assert.True(MapPlan.Allow(rules, plan.Allows[0]));
        Assert.Equal(["192.0.2.200", "10.0.0.2"], rules.Rules[0].Sources);
        Assert.Equal(mid.Id, Assert.Single(rules.Rules[0].Links).ServerId);
        Assert.True(MapPlan.Allow(rules, plan.Allows[0])); // twice: no duplicate
        Assert.Equal(2, rules.Rules[0].Sources.Count);

        plan.Cancel(planned);
        Assert.Empty(plan.Allows);
        Assert.Equal(1, plan.Count);
    }
}
