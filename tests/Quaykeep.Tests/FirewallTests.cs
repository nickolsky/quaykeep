using System.Net;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public class FirewallTests
{
    private static FirewallRule Rule(FirewallAction a, string ports, params string[] sources) =>
        new() { Action = a, Ports = ports, Sources = [.. sources], Protocol = "tcp" };

    private static FirewallConfig Config(params FirewallRule[] rules) => new() { Rules = [.. rules] };

    private static List<string> Lines(string payload, string chain) =>
        payload.Split('\n').Where(l => l.StartsWith($"-A {chain} ")).Select(l => l[(chain.Length + 4)..]).ToList();

    [Fact]
    public void Payload_Orders_Blocks_Then_Port_Allow_Lists_Then_The_Whole_Server()
    {
        var whole = Rule(FirewallAction.AllowOnly, "", "198.51.100.0/24");
        var allow = Rule(FirewallAction.AllowOnly, "8443", "203.0.113.9");
        var block = Rule(FirewallAction.Block, "22", "203.0.113.5");
        var c = Config(whole, allow, block);
        var p = FirewallRules.Payload(c, v6: false);

        Assert.StartsWith("*filter\n:QK-IN - [0:0]\n:QK-FWD - [0:0]\n", p);
        Assert.EndsWith("COMMIT\n", p);
        var inRules = Lines(p, "QK-IN");
        Assert.Equal($"-i lo -m comment --comment \"qk-fw:{FirewallRules.Hash(c)}\" -j RETURN", inRules[0]);
        Assert.Equal("-m conntrack --ctstate ESTABLISHED,RELATED -j RETURN", inRules[1]);
        Assert.Equal("-p udp --sport 67 --dport 68 -j RETURN", inRules[2]); // DHCP keeps the lease even with an allow-list
        Assert.Equal([
            $"-p tcp -s 203.0.113.5 --dport 22 -m comment --comment \"qk:{block.Id}\" -j DROP",
            $"-p tcp -s 203.0.113.9 --dport 8443 -m comment --comment \"qk:{allow.Id}\" -j RETURN",
            $"-p tcp --dport 8443 -m comment --comment \"qk:{allow.Id}\" -j DROP",
            $"-s 198.51.100.0/24 -m comment --comment \"qk:{whole.Id}\" -j RETURN",
            $"-m comment --comment \"qk:{whole.Id}\" -j DROP",
        ], inRules.Skip(3));

        // connections DNAT-ed to containers / forwards: matched by the port before DNAT, containers' own traffic left alone
        var fwd = Lines(p, "QK-FWD");
        Assert.Equal("-m conntrack ! --ctstate DNAT -j RETURN", fwd[1]);
        Assert.Contains($"-p tcp -s 203.0.113.5 -m conntrack --ctorigdstport 22 -m comment --comment \"qk:{block.Id}\" -j DROP", fwd);
        Assert.Contains($"-m comment --comment \"qk:{whole.Id}\" -j DROP", fwd);
    }

    [Fact]
    public void Families_Split_And_Ipv6_Keeps_Neighbour_Discovery()
    {
        var allow = Rule(FirewallAction.AllowOnly, "443,8000-8100", "203.0.113.9", "2001:db8::/32");
        allow.Protocol = "tcp,udp";
        var c = Config(allow);
        var v4 = FirewallRules.Payload(c, v6: false);
        var v6 = FirewallRules.Payload(c, v6: true);
        Assert.DoesNotContain("2001:db8", v4);
        Assert.DoesNotContain("203.0.113.9", v6);
        Assert.Contains("-A QK-IN -p ipv6-icmp -j RETURN", v6);
        Assert.Contains("-A QK-IN -p udp --sport 547 --dport 546 -j RETURN", v6);
        Assert.Contains("-p udp -s 2001:db8::/32 --dport 8000:8100", v6); // ranges written the iptables way
        Assert.Equal(4, Lines(v4, "QK-IN").Count(l => l.EndsWith("-j DROP"))); // 2 protocols × 2 port items

        // an allow-list with IPv4 addresses only closes the port to all of IPv6
        var only4 = Config(Rule(FirewallAction.AllowOnly, "22", "203.0.113.9"));
        Assert.Contains("-A QK-IN -p tcp --dport 22 -m comment", FirewallRules.Payload(only4, v6: true));
    }

    [Fact]
    public void Disabled_Rules_Are_Left_Out_And_Change_The_Hash()
    {
        var r = Rule(FirewallAction.Block, "", "203.0.113.5");
        var c = Config(r);
        var on = FirewallRules.Hash(c);
        Assert.Contains("-s 203.0.113.5 -m comment", FirewallRules.Payload(c, false));
        r.Enabled = false;
        Assert.NotEqual(on, FirewallRules.Hash(c));
        Assert.DoesNotContain("203.0.113.5", FirewallRules.Payload(c, false));
        r.Enabled = true;
        r.Sources = ["203.0.113.5/32"]; // the same address written another way
        Assert.Equal(on, FirewallRules.Hash(c));
    }

    [Theory]
    [InlineData("tcp", "443", "1.2.3.4; rm -rf /")]
    [InlineData("tcp", "443", "1.2.3.4 -j ACCEPT")]
    [InlineData("tcp", "443", "1.2.3.4/33")]
    [InlineData("tcp", "443", "::1/129")]
    [InlineData("tcp", "443", "fe80::1%eth0")]
    [InlineData("tcp", "443", "`id`")]
    [InlineData("tcp", "443", "example.com")]
    [InlineData("icmp", "443", "1.2.3.4")]
    [InlineData("tcp; reboot", "443", "1.2.3.4")]
    [InlineData("tcp", "70000", "1.2.3.4")]
    [InlineData("tcp", "443$(id)", "1.2.3.4")]
    [InlineData("tcp", "9000:8000", "1.2.3.4")]
    [InlineData("tcp", "0", "1.2.3.4")]
    public void Validate_Rejects_Bad_Input(string protocol, string ports, string source)
    {
        var r = new FirewallRule { Protocol = protocol, Ports = ports, Sources = [source] };
        Assert.Throws<ArgumentException>(() => FirewallRules.Validate(r));
        Assert.Throws<ArgumentException>(() => FirewallRules.Apply(Config(r), 60));
    }

    [Fact]
    public void Validate_Accepts_Addresses_Networks_And_Port_Lists()
    {
        FirewallRules.Validate(new FirewallRule { Ports = "22, 80,443 8000-8100", Sources = ["203.0.113.5", "10.1.2.3/8", "2001:db8::/32", "::1"] });
        FirewallRules.Validate(new FirewallRule { Ports = "", Protocol = "anything", Sources = ["0.0.0.0/0"] }); // whole server: no protocol
        Assert.Throws<ArgumentException>(() => FirewallRules.Validate(new FirewallRule { Ports = "22", Sources = [] }));
        Assert.Equal("10.0.0.0/8", FirewallRules.Canonical("10.1.2.3/8"));
        Assert.Equal("203.0.113.5", FirewallRules.Canonical("203.0.113.5/32"));
        Assert.Equal(["1.2.3.4", "10.0.0.0/8", "2001:db8::1"], FirewallRules.ParseSources("1.2.3.4, 10.0.0.0/8\n2001:db8::1;1.2.3.4"));
        Assert.Equal("443,8000:8100", FirewallRules.NormalizePorts(" 443 ; 8000-8100 "));
    }

    [Theory]
    [InlineData(FirewallAction.Block, "22", "203.0.113.5", true)]
    [InlineData(FirewallAction.Block, "443", "203.0.113.5", false)]
    [InlineData(FirewallAction.Block, "", "203.0.113.0/24", true)]
    [InlineData(FirewallAction.AllowOnly, "22", "198.51.100.1", true)]
    [InlineData(FirewallAction.AllowOnly, "20:30", "203.0.113.0/24", false)]
    [InlineData(FirewallAction.AllowOnly, "", "198.51.100.1", true)]
    [InlineData(FirewallAction.AllowOnly, "", "203.0.113.5", false)]
    [InlineData(FirewallAction.AllowOnly, "443", "198.51.100.1", false)]
    public void Cuts_Ssh_For_This_Pc(FirewallAction action, string ports, string source, bool cuts) =>
        Assert.Equal(cuts, FirewallRules.CutsSsh(Config(Rule(action, ports, source)), 22, IPAddress.Parse("203.0.113.5")));

    [Fact]
    public void A_Port_Allow_List_With_This_Pc_Wins_Over_The_Whole_Server_List()
    {
        var c = Config(Rule(FirewallAction.AllowOnly, "", "198.51.100.1"), Rule(FirewallAction.AllowOnly, "22", "203.0.113.5"));
        Assert.False(FirewallRules.CutsSsh(c, 22, IPAddress.Parse("::ffff:203.0.113.5")));
        c.Rules[1].Enabled = false;
        Assert.True(FirewallRules.CutsSsh(c, 22, IPAddress.Parse("203.0.113.5")));
    }

    [Fact]
    public void State_Is_Read_Back_From_The_Chains()
    {
        const string text = """
            -P INPUT ACCEPT
            -A INPUT -j QK-IN
            -A INPUT -j ufw-before-input
            -N QK-IN
            -A QK-IN -i lo -m comment --comment "qk-fw:0123456789ab" -j RETURN
            -A QK-IN -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN
            -A QK-IN -s 203.0.113.5/32 -p tcp -m tcp --dport 22 -m comment --comment "qk:aa11bb22" -j DROP
            -A QK-IN -p tcp -m tcp --dport 443 -m comment --comment qk:cc33dd44 -j DROP
            """;
        var s = FirewallRules.ParseState(text)!;
        Assert.True(s.Installed);
        Assert.Equal("0123456789ab", s.Hash);
        Assert.Equal(["aa11bb22", "cc33dd44"], s.RuleIds);

        var none = FirewallRules.ParseState("-P INPUT ACCEPT\n-A INPUT -j ufw-before-input\n")!;
        Assert.False(none.Installed);
        Assert.Null(none.Hash);
        Assert.Null(FirewallRules.ParseState("iptables v1.8.9 (nf_tables): Permission denied (you must be root)\n")); // not root: unknown
    }

    [Fact]
    public void Inventory_Reads_The_Firewall_With_The_Same_Command()
    {
        var script = typeof(Quaykeep.Core.Inventory.ServerInventoryService).GetField("Script",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as string;
        Assert.Contains("echo '@@sshm:" + FirewallRules.Section + "'; " + FirewallRules.ReadScript + ";", script);
    }

    [Fact]
    public void Scripts_Are_Posix_Sh()
    {
        var c = Config(Rule(FirewallAction.Block, "22,443", "203.0.113.5", "2001:db8::1"), Rule(FirewallAction.AllowOnly, "", "198.51.100.0/24"));
        Bash.CheckSyntax(FirewallRules.Apply(c, 90), posix: true);
        Bash.CheckSyntax(FirewallRules.Confirm, posix: true);
        Bash.CheckSyntax(FirewallRules.RevertNow, posix: true);
        Bash.CheckSyntax(FirewallRules.TurnOff, posix: true);
        Assert.DoesNotContain("[[ ", FirewallRules.Apply(c, 90));
        // the timer is armed before the new rules are loaded, and the old ones come back when loading fails
        var apply = FirewallRules.Apply(c, 90);
        Assert.True(apply.IndexOf("--on-active=90", StringComparison.Ordinal) < apply.IndexOf("iptables-restore --noflush < \"$D/new.iptables\"", StringComparison.Ordinal));
        Assert.Contains("sh \"$D/revert.sh\"", apply[apply.IndexOf("could not be loaded", StringComparison.Ordinal)..]);
    }
}
