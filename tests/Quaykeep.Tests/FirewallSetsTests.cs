using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public class FirewallSetsTests
{
    private static FirewallRule Rule(params string[] sources) =>
        new() { Action = FirewallAction.AllowOnly, Ports = "22", Protocol = "tcp", Sources = [.. sources] };

    [Fact]
    public void Presets_Follow_The_Own_Rules_And_Change_The_Hash()
    {
        var office = new FirewallPreset { Name = "office", Rules = [Rule("198.51.100.0/24")] };
        var web = new FirewallPreset { Name = "web", Rules = [new FirewallRule { Action = FirewallAction.Block, Ports = "80", Sources = ["203.0.113.5"] }] };
        var own = new FirewallConfig { Rules = [Rule("203.0.113.9")], Presets = [web.Id, office.Id, Guid.NewGuid()] }; // a deleted preset is skipped
        var e = FirewallSets.Effective(own, [office, web]);
        Assert.Equal(["203.0.113.9", "203.0.113.5", "198.51.100.0/24"], e.Rules.Select(r => r.Sources[0]));
        Assert.Empty(e.Presets);

        var before = FirewallRules.Hash(e);
        office.Rules[0].Sources.Add("192.0.2.1"); // editing the preset changes what every server using it should have
        Assert.NotEqual(before, FirewallRules.Hash(FirewallSets.Effective(own, [office, web])));
        Assert.Empty(FirewallSets.Effective(null, [office]).Rules);
    }

    [Fact]
    public void A_Changed_Address_Is_Found_In_Rules_Presets_And_Forwards()
    {
        var target = new ServerEntry { Name = "vpn", Host = "203.0.113.9" };
        var linked = Rule("203.0.113.9", "198.51.100.1");
        linked.Links.Add(new FirewallLink { ServerId = target.Id, Address = "203.0.113.9" });
        var typed = Rule("203.0.113.9/32");
        var other = Rule("198.51.100.2");
        var edge = new ServerEntry
        {
            Name = "edge", Firewall = new FirewallConfig { Rules = [linked, other] },
            Facts = new ServerFacts { Forwards = [new PortForward { Protocol = "tcp", ListenPort = "443", TargetIp = "203.0.113.9" },
                new PortForward { Protocol = "tcp", ListenPort = "80", TargetIp = "198.51.100.7" }] },
        };
        var data = new VaultData { Servers = [target, edge], FirewallPresets = [new FirewallPreset { Name = "p", Rules = [typed] }] };

        var uses = FirewallSets.UsesOf(data, target.Id, "203.0.113.9");
        Assert.Equal(2, uses.Count);
        Assert.Contains(uses, u => u.Rule == linked && u.Linked && u.ServerId == edge.Id);
        Assert.Contains(uses, u => u.Rule == typed && !u.Linked && u.PresetId != null);
        Assert.Equal("443", Assert.Single(FirewallSets.ForwardsTo(data, "203.0.113.9")).Forward.ListenPort);

        FirewallSets.Replace(linked, target.Id, "203.0.113.9", "192.0.2.50");
        Assert.Equal(["192.0.2.50", "198.51.100.1"], linked.Sources);
        Assert.Equal("192.0.2.50", Assert.Single(linked.Links).Address);
        FirewallSets.Replace(typed, target.Id, "203.0.113.9", "192.0.2.50"); // a typed one becomes linked
        Assert.Equal(target.Id, Assert.Single(typed.Links).ServerId);
        Assert.Empty(FirewallSets.UsesOf(data, target.Id, "203.0.113.9"));
    }

    [Fact]
    public void Links_Go_With_Their_Address()
    {
        var r = Rule("203.0.113.9");
        r.Links.Add(new FirewallLink { ServerId = Guid.NewGuid(), Address = "203.0.113.9" });
        r.Links.Add(new FirewallLink { ServerId = Guid.NewGuid(), Address = "198.51.100.1" }); // removed from the sources
        FirewallSets.PruneLinks(r);
        Assert.Equal("203.0.113.9", Assert.Single(r.Links).Address);
        var copy = r.Clone();
        copy.Links[0].Address = "x";
        Assert.Equal("203.0.113.9", r.Links[0].Address);
    }
}
